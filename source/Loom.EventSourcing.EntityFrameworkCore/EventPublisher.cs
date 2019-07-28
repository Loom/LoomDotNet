﻿namespace Loom.EventSourcing.EntityFrameworkCore
{
    using System;
    using System.Collections.Generic;
    using System.Collections.Immutable;
    using System.Linq;
    using System.Threading.Tasks;
    using Loom.Messaging;
    using Microsoft.EntityFrameworkCore;

    internal sealed class EventPublisher
    {
        private readonly Func<EventStoreContext> _contextFactory;
        private readonly TypeResolver _typeResolver;
        private readonly IMessageBus _eventBus;

        public EventPublisher(Func<EventStoreContext> contextFactory,
                              TypeResolver typeResolver,
                              IMessageBus eventBus)
        {
            _contextFactory = contextFactory;
            _typeResolver = typeResolver;
            _eventBus = eventBus;
        }

        public async Task PublishEvents(string stateType, Guid streamId)
        {
            using (EventStoreContext context = _contextFactory.Invoke())
            {
                await FlushEvents(context, stateType, streamId).ConfigureAwait(continueOnCapturedContext: false);
            }
        }

        private async Task FlushEvents(EventStoreContext context,
                                       string stateType,
                                       Guid streamId)
        {
            IQueryable<PendingEvent> query = context.GetPendingEventsQuery(stateType, streamId);
            List<PendingEvent> pendingEvents = await query.ToListAsync().ConfigureAwait(continueOnCapturedContext: false);
            foreach (IEnumerable<PendingEvent> window in Window(pendingEvents))
            {
                string partitionKey = $"{streamId}";
                await _eventBus.Send(window.Select(GenerateMessage), partitionKey).ConfigureAwait(continueOnCapturedContext: false);
                context.PendingEvents.RemoveRange(window);
                await context.SaveChangesAsync().ConfigureAwait(continueOnCapturedContext: false);
            }
        }

        private static IEnumerable<IEnumerable<PendingEvent>> Window(
            IEnumerable<PendingEvent> pendingEvents)
        {
            Guid transaction = default;
            List<PendingEvent> fragment = null;

            foreach (PendingEvent pendingEvent in pendingEvents)
            {
                if (fragment == null)
                {
                    transaction = pendingEvent.Transaction;
                    fragment = new List<PendingEvent> { pendingEvent };
                    continue;
                }

                if (pendingEvent.Transaction != transaction)
                {
                    yield return fragment.ToImmutableArray();

                    transaction = pendingEvent.Transaction;
                    fragment = new List<PendingEvent> { pendingEvent };
                    continue;
                }

                fragment.Add(pendingEvent);
            }

            if (fragment != null)
            {
                yield return fragment.ToImmutableArray();
            }
        }

        private Message GenerateMessage(PendingEvent entity) => entity.GenerateMessage(_typeResolver);
    }
}
