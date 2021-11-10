﻿using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Loom.Messaging;

namespace Loom.EventSourcing
{
    internal class DelegatingEventReader : IEventReader
    {
        private readonly Func<string, long, Task<IEnumerable<object>>> _function;

        public DelegatingEventReader(
            Func<string, long, Task<IEnumerable<object>>> function)
        {
            _function = function;
        }

        public Task<IEnumerable<object>> QueryEvents(
            string streamId,
            long fromVersion,
            CancellationToken cancellationToken = default)
        {
            return _function.Invoke(streamId, fromVersion);
        }

        public Task<IEnumerable<Message>> QueryEventMessages(
            string streamId,
            CancellationToken cancellationToken = default)
        {
            throw new NotImplementedException();
        }
    }
}
