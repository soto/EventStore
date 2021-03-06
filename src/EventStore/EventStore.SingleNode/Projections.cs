﻿// Copyright (c) 2012, Event Store LLP
// All rights reserved.
// 
// Redistribution and use in source and binary forms, with or without
// modification, are permitted provided that the following conditions are
// met:
// 
// Redistributions of source code must retain the above copyright notice,
// this list of conditions and the following disclaimer.
// Redistributions in binary form must reproduce the above copyright
// notice, this list of conditions and the following disclaimer in the
// documentation and/or other materials provided with the distribution.
// Neither the name of the Event Store LLP nor the names of its
// contributors may be used to endorse or promote products derived from
// this software without specific prior written permission
// THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS
// "AS IS" AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT
// LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR
// A PARTICULAR PURPOSE ARE DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT
// HOLDER OR CONTRIBUTORS BE LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL,
// SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT
// LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES; LOSS OF USE,
// DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON ANY
// THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
// (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE
// OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
// 

using System.Collections.Generic;
using System.Linq;
using EventStore.Core.Bus;
using EventStore.Core.Messages;
using EventStore.Core.Messaging;
using EventStore.Core.Services.TimerService;
using EventStore.Core.Services.Transport.Http;
using EventStore.Core.TransactionLog.Chunks;
using EventStore.Projections.Core;
using EventStore.Projections.Core.Messages;
using EventStore.Projections.Core.Messaging;
using EventStore.Projections.Core.Services.Processing;

namespace EventStore.SingleNode
{
    public class Projections
    {
        private List<QueuedHandler> _coreQueues;
        private readonly int _projectionWorkerThreadCount;
        private QueuedHandler _managerInputQueue;
        private InMemoryBus _managerInputBus;
        private ProjectionManagerNode _projectionManagerNode;

        public Projections(
            TFChunkDb db, QueuedHandler mainQueue, InMemoryBus mainBus, TimerService timerService,
            HttpService httpService, int projectionWorkerThreadCount)
        {
            _projectionWorkerThreadCount = projectionWorkerThreadCount;
            SetupMessaging(db, mainQueue, mainBus, timerService, httpService);
        }

        private void SetupMessaging(
            TFChunkDb db, QueuedHandler mainQueue, InMemoryBus mainBus, TimerService timerService,
            HttpService httpService)
        {
            _coreQueues = new List<QueuedHandler>();
            _managerInputBus = new InMemoryBus("manager input bus");
            _managerInputQueue = new QueuedHandler(_managerInputBus, "ProjectionManager");

            while (_coreQueues.Count < _projectionWorkerThreadCount)
            {
                var coreInputBus = new InMemoryBus("bus");
                var coreQueue = new QueuedHandler(coreInputBus, "ProjectionCoreQueue #" + _coreQueues.Count);
                var projectionNode = new ProjectionWorkerNode(db, coreQueue);
                projectionNode.SetupMessaging(coreInputBus);


                var forwarder = new RequestResponseQueueForwarder(
                    inputQueue: coreQueue, externalRequestQueue: mainQueue);
                // forwarded messages
                projectionNode.CoreOutput.Subscribe<ClientMessage.ReadEvent>(forwarder);
                projectionNode.CoreOutput.Subscribe<ClientMessage.ReadStreamEventsBackward>(forwarder);
                projectionNode.CoreOutput.Subscribe<ClientMessage.ReadStreamEventsForward>(forwarder);
                projectionNode.CoreOutput.Subscribe<ClientMessage.ReadAllEventsForward>(forwarder);
                projectionNode.CoreOutput.Subscribe<ClientMessage.WriteEvents>(forwarder);

                projectionNode.CoreOutput.Subscribe(
                    Forwarder.Create<ProjectionMessage.Projections.Management.StateReport>(_managerInputQueue));
                projectionNode.CoreOutput.Subscribe(
                    Forwarder.Create<ProjectionMessage.Projections.Management.StatisticsReport>(_managerInputQueue));
                projectionNode.CoreOutput.Subscribe(
                    Forwarder.Create<ProjectionMessage.Projections.StatusReport.Started>(_managerInputQueue));
                projectionNode.CoreOutput.Subscribe(
                    Forwarder.Create<ProjectionMessage.Projections.StatusReport.Stopped>(_managerInputQueue));
                projectionNode.CoreOutput.Subscribe(
                    Forwarder.Create<ProjectionMessage.Projections.StatusReport.Faulted>(_managerInputQueue));

                projectionNode.CoreOutput.Subscribe(timerService);


                projectionNode.CoreOutput.Subscribe(Forwarder.Create<Message>(coreQueue)); // forward all

                coreInputBus.Subscribe(new UnwrapEnvelopeHandler());

                _coreQueues.Add(coreQueue);
            }


            _projectionManagerNode = ProjectionManagerNode.Create(
                db, _managerInputQueue, httpService, _coreQueues.Cast<IPublisher>().ToArray());
            _projectionManagerNode.SetupMessaging(_managerInputBus);
            {
                var forwarder = new RequestResponseQueueForwarder(
                    inputQueue: _managerInputQueue, externalRequestQueue: mainQueue);
                _projectionManagerNode.Output.Subscribe<ClientMessage.ReadEvent>(forwarder);
                _projectionManagerNode.Output.Subscribe<ClientMessage.ReadStreamEventsBackward>(forwarder);
                _projectionManagerNode.Output.Subscribe<ClientMessage.ReadStreamEventsForward>(forwarder);
                _projectionManagerNode.Output.Subscribe<ClientMessage.WriteEvents>(forwarder);
                _projectionManagerNode.Output.Subscribe(Forwarder.Create<Message>(_managerInputQueue));
                    // self forward all

                mainBus.Subscribe(Forwarder.Create<SystemMessage.SystemInit>(_managerInputQueue));
                mainBus.Subscribe(Forwarder.Create<SystemMessage.SystemStart>(_managerInputQueue));
                mainBus.Subscribe(Forwarder.Create<SystemMessage.StateChangeMessage>(_managerInputQueue));
                _managerInputBus.Subscribe(new UnwrapEnvelopeHandler());
            }
        }

        public void Start()
        {
            _managerInputQueue.Start();
            foreach (var queue in _coreQueues)
                queue.Start();
        }
    }
}
