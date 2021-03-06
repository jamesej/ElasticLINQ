﻿// Licensed under the Apache 2.0 License. See LICENSE.txt in the project root for more information.

using ElasticLinq.Async;
using ElasticLinq.Logging;
using ElasticLinq.Mapping;
using ElasticLinq.Request;
using ElasticLinq.Request.Criteria;
using ElasticLinq.Request.Visitors;
using ElasticLinq.Response.Model;
using ElasticLinq.Retry;
using ElasticLinq.Utility;
using System;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;

namespace ElasticLinq
{
    /// <summary>
    /// Query provider implementation for Elasticsearch.
    /// </summary>
    public sealed class ElasticQueryProvider : IQueryProvider, IAsyncQueryExecutor
    {
        readonly ElasticRequestProcessor requestProcessor;

        /// <summary>
        /// Create a new ElasticQueryProvider for a given connection, mapping, log, retry policy and field prefix.
        /// </summary>
        /// <param name="connection">Connection to use to connect to Elasticsearch.</param>
        /// <param name="mapping">A mapping to specify how queries and results are translated.</param>
        /// <param name="log">A log to receive any information or debugging messages.</param>
        /// <param name="retryPolicy">A policy to describe how to handle network issues.</param>
        public ElasticQueryProvider(IElasticConnection connection, IElasticMapping mapping, ILog log, IRetryPolicy retryPolicy)
        {
            Argument.EnsureNotNull(nameof(connection), connection);
            Argument.EnsureNotNull(nameof(mapping), mapping);
            Argument.EnsureNotNull(nameof(log), log);
            Argument.EnsureNotNull(nameof(retryPolicy), retryPolicy);

            Connection = connection;
            Mapping = mapping;
            Log = log;
            RetryPolicy = retryPolicy;

            requestProcessor = new ElasticRequestProcessor(connection, mapping, log, retryPolicy);
        }

        internal IElasticConnection Connection { get; private set; }

        internal ILog Log { get; }

        internal IElasticMapping Mapping { get; }

        internal IRetryPolicy RetryPolicy { get; private set; }

        /// <inheritdoc/>
        public IQueryable<T> CreateQuery<T>(Expression expression)
        {
            Argument.EnsureNotNull(nameof(expression), expression);

            if (!typeof(IQueryable<T>).IsAssignableFrom(expression.Type))
                throw new ArgumentOutOfRangeException(nameof(expression));

            return new ElasticQuery<T>(this, expression);
        }

        /// <inheritdoc/>
        public IQueryable CreateQuery(Expression expression)
        {
            Argument.EnsureNotNull(nameof(expression), expression);

            var elementType = TypeHelper.GetSequenceElementType(expression.Type);
            var queryType = typeof(ElasticQuery<>).MakeGenericType(elementType);
            try
            {
                return (IQueryable)Activator.CreateInstance(queryType, this, expression);
            }
            catch (TargetInvocationException ex)
            {
                ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
                return null;  // Never called, as the above code re-throws
            }
        }

        /// <inheritdoc/>
        public TResult Execute<TResult>(Expression expression)
        {
            return (TResult)Execute(expression);
        }

        /// <inheritdoc/>
        public object Execute(Expression expression)
        {
            return AsyncHelper.RunSync(() => ExecuteAsync(expression));
        }

        /// <inheritdoc/>
        public async Task<TResult> ExecuteAsync<TResult>(Expression expression, CancellationToken cancellationToken = default(CancellationToken))
        {
            return (TResult)await ExecuteAsync(expression, cancellationToken);
        }

        /// <inheritdoc/>
        public async Task<object> ExecuteAsync(Expression expression, CancellationToken cancellationToken = default(CancellationToken))
        {
            Argument.EnsureNotNull(nameof(expression), expression);

            var translation = ElasticQueryTranslator.Translate(Mapping, expression);

            Log.Debug(null, null, "Executing query against document '{0}'", translation.SearchRequest.DocumentType);

            try
            {
                ElasticResponse response;
                if (translation.SearchRequest.Filter == ConstantCriteria.False)
                {
                    response = new ElasticResponse();
                }
                else
                {
                    response = await requestProcessor.SearchAsync(translation.SearchRequest, cancellationToken);
                    if (response == null)
                        throw new InvalidOperationException("No HTTP response received.");
                }

                var result = translation.Materializer.Materialize(response);

                if (response.hits != null)
                {
                    var hits = response.hits.hits;
                    if (hits != null && hits.Capacity > 4096)
                    {
                        // If the response has a large number of hits then the List<T> instance might end up allocating an array on the large object heap.
                        // This means that the elements in that array can survive multiple rounds of garbage collection, even if they are not actually
                        // reachable anymore.
                        // Clearing out the collection after we materialized the result means the elements can be freed up.
                        hits.Clear();
                    }
                }

                return result;
            }
            catch (AggregateException ex)
            {
                ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
                return null;  // Never called, as the above code re-throws
            }
        }
    }
}
