using System;
using System.Threading;
using System.Threading.Tasks;
using Eventuous.Projections.MongoDB.Tools;
using Eventuous.Subscriptions;
using JetBrains.Annotations;
using Microsoft.Extensions.Logging;
using MongoDB.Driver;

namespace Eventuous.Projections.MongoDB {
    [PublicAPI]
    public abstract class MongoProjection<T> : IEventHandler
        where T : ProjectedDocument {
        readonly ILogger? _log;

        protected IMongoCollection<T> Collection { get; }

        protected MongoProjection(IMongoDatabase database, string subscriptionGroup, ILoggerFactory? loggerFactory) {
            var log = loggerFactory?.CreateLogger(GetType());
            _log           = log?.IsEnabled(LogLevel.Debug) == true ? log : null;
            SubscriptionId = Ensure.NotEmptyString(subscriptionGroup, nameof(subscriptionGroup));
            Collection     = Ensure.NotNull(database, nameof(database)).GetDocumentCollection<T>();
        }

        public string SubscriptionId { get; }

        public async Task HandleEvent(object evt, long? position, CancellationToken cancellationToken) {
            var updateTask = GetUpdate(evt);
            var update     = updateTask == NoOp ? null : await updateTask.Ignore();

            if (update == null) {
                _log?.LogDebug("No handler for {Event}", evt.GetType().Name);
                return;
            }

            _log?.LogDebug("Projecting {Event}", evt.GetType().Name);

            var task = update switch {
                OtherOperation<T> operation => operation.Task,
                CollectionOperation<T> col  => col.Execute(Collection, cancellationToken),
                UpdateOperation<T> upd      => ExecuteUpdate(upd),
                _                           => Task.CompletedTask
            };

            await task.Ignore();

            Task ExecuteUpdate(UpdateOperation<T> upd)
                => Collection.UpdateOneAsync(
                    upd.Filter,
                    upd.Update.Set(x => x.Position, position),
                    new UpdateOptions { IsUpsert = true },
                    cancellationToken
                );
        }

        protected abstract ValueTask<Operation<T>> GetUpdate(object evt);

        protected Operation<T> UpdateOperation(BuildFilter<T> filter, BuildUpdate<T> update)
            => new UpdateOperation<T>(filter(Builders<T>.Filter), update(Builders<T>.Update));

        protected ValueTask<Operation<T>> UpdateOperationTask(BuildFilter<T> filter, BuildUpdate<T> update)
            => new(UpdateOperation(filter, update));

        protected Operation<T> UpdateOperation(string id, BuildUpdate<T> update)
            => UpdateOperation(filter => filter.Eq(x => x.Id, id), update);

        protected ValueTask<Operation<T>> UpdateOperationTask(string id, BuildUpdate<T> update)
            => new(UpdateOperation(id, update));

        protected static readonly ValueTask<Operation<T>> NoOp = new((Operation<T>) null!);
    }

    public abstract record Operation<T>;

    public record UpdateOperation<T>(FilterDefinition<T> Filter, UpdateDefinition<T> Update) : Operation<T>;

    public record OtherOperation<T>(Task Task) : Operation<T>;

    public record CollectionOperation<T>(Func<IMongoCollection<T>, CancellationToken, Task> Execute) : Operation<T>;
}