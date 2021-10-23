﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Loom.Json;
using Loom.Messaging;
using Microsoft.Azure.Cosmos.Table;

namespace Loom.EventSourcing.Azure
{
    public class TableEventStore<T> :
        IEventStore<T>,
        IEventCollector,
        IEventReader
    {
        private readonly CloudTable _table;
        private readonly TypeResolver _typeResolver;
        private readonly IJsonProcessor _jsonProcessor;
        private readonly EventPublisher _publisher;

        public TableEventStore(CloudTable table,
                               TypeResolver typeResolver,
                               IJsonProcessor jsonProcessor,
                               IMessageBus eventBus)
        {
            _table = table;
            _typeResolver = typeResolver;
            _jsonProcessor = jsonProcessor;
            _publisher = new EventPublisher(table, typeResolver, jsonProcessor, eventBus);
        }

        public Task CollectEvents(string processId,
                                  string? initiator,
                                  string? predecessorId,
                                  Guid streamId,
                                  long startVersion,
                                  IEnumerable<object> events)
        {
            return SaveAndPublish(stateType: ResolveName(typeof(T)),
                                  processId,
                                  initiator,
                                  predecessorId,
                                  transaction: Guid.NewGuid(),
                                  streamId,
                                  startVersion,
                                  events.ToList().AsReadOnly());
        }

        [Obsolete("Use metadata decapsulated overload instead.")]
        public Task CollectEvents(Guid streamId,
                                  long startVersion,
                                  IEnumerable<object> events,
                                  TracingProperties tracingProperties = default)
        {
            return SaveAndPublish(stateType: ResolveName(typeof(T)),
                                  processId: tracingProperties.OperationId,
                                  initiator: tracingProperties.Contributor,
                                  predecessorId: tracingProperties.ParentId,
                                  transaction: Guid.NewGuid(),
                                  streamId,
                                  startVersion,
                                  events.ToList().AsReadOnly());
        }

        private async Task SaveAndPublish(string stateType,
                                          string processId,
                                          string? initiator,
                                          string? predecessorId,
                                          Guid transaction,
                                          Guid streamId,
                                          long startVersion,
                                          IReadOnlyList<object> events)
        {
            if (events.Count == 0)
            {
                return;
            }

            await SaveQueueTicket().ConfigureAwait(continueOnCapturedContext: false);
            await SaveEvents().ConfigureAwait(continueOnCapturedContext: false);
            await PublishPendingEvents().ConfigureAwait(continueOnCapturedContext: false);

            Task SaveQueueTicket()
            {
                var queueTicket = new QueueTicket(stateType, streamId, startVersion, events.Count, transaction);
                return _table.ExecuteAsync(TableOperation.Insert(queueTicket));
            }

            Task SaveEvents()
            {
                var batch = new TableBatchOperation();

                for (int i = 0; i < events.Count; i++)
                {
                    object source = events[i];

                    var streamEvent = new StreamEvent(
                        stateType,
                        streamId,
                        version: startVersion + i,
                        raisedTimeUtc: DateTime.UtcNow,
                        eventType: ResolveName(source.GetType()),
                        payload: _jsonProcessor.ToJson(source),
                        messageId: $"{Guid.NewGuid()}",
                        processId,
                        initiator,
                        predecessorId,
                        transaction);

                    batch.Insert(streamEvent);
                }

                return _table.ExecuteBatchAsync(batch);
            }

            Task PublishPendingEvents() => _publisher.PublishEvents(stateType, streamId);
        }

        public async Task<IEnumerable<object>> QueryEvents(Guid streamId, long fromVersion)
        {
            CancellationToken cancellationToken = CancellationToken.None;
            IEnumerable<StreamEvent> source = await GetEntities(streamId, fromVersion, cancellationToken)
                                                   .ConfigureAwait(continueOnCapturedContext: false);
            return source.Select(RestorePayload).ToList().AsReadOnly();
        }

        public async Task<IEnumerable<Message>> QueryEventMessages(
            Guid streamId,
            CancellationToken cancellationToken = default)
        {
            long fromVersion = 1;
            IEnumerable<StreamEvent> source = await GetEntities(streamId, fromVersion, cancellationToken)
                                                   .ConfigureAwait(continueOnCapturedContext: false);
            return source.Select(GenerateMessage).ToList().AsReadOnly();
        }

        private Task<IEnumerable<StreamEvent>> GetEntities(
            Guid streamId,
            long fromVersion,
            CancellationToken cancellationToken)
        {
            string stateType = ResolveName(typeof(T));
            return _table.BuildStreamEventQuery(stateType, streamId, fromVersion).ExecuteAsync(cancellationToken);
        }

        private string ResolveName(Type type)
            => _typeResolver.TryResolveTypeName(type)
            ?? throw new InvalidOperationException($"Could not resolve the name of type {type}.");

        private object RestorePayload(StreamEvent entity)
            => entity.RestorePayload(_typeResolver, _jsonProcessor);

        private Message GenerateMessage(StreamEvent entity)
            => entity.GenerateMessage(_typeResolver, _jsonProcessor);
    }
}
