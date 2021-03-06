﻿using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNet.SignalR.Client;

namespace Microsoft.AspNet.SignalR.DCrank.Crank
{
    public class Worker : IWorker
    {
        private readonly Process _agentProcess;
        private readonly IAgent _agent;
        private readonly int _processId;
        private readonly ConcurrentBag<Client> _clients;
        private readonly CancellationTokenSource _sendStatusCts;
        private int _targetConnectionCount;

        public Worker(int agentProcessId)
        {
            _agentProcess = Process.GetProcessById(agentProcessId);
            _agent = new AgentSender(new StreamWriter(Console.OpenStandardOutput()));
            _processId = Process.GetCurrentProcess().Id;
            _clients = new ConcurrentBag<Client>();
            _sendStatusCts = new CancellationTokenSource();
        }

        public async Task Run()
        {
            _agentProcess.EnableRaisingEvents = true;
            _agentProcess.Exited += OnExited;

            Log("Worker created");

            var receiver = new WorkerReceiver(
                new StreamReader(Console.OpenStandardInput()),
                this);

            receiver.Start();

            await SendStatusUpdate(_sendStatusCts.Token);

            receiver.Stop();
        }

        public async Task Ping(int value)
        {
            Log("Worker received ping command with value {0}.", value);

            await _agent.Pong(_processId, value);
            Log("Worker sent pong command with value {0}.", value);
        }

        public async Task Connect(string targetAddress, int numberOfConnections)
        {
            Log("Worker received connect command with target address {0} and number of connections {1}", targetAddress, numberOfConnections);
            var connectArguments = new CrankArguments()
            {
                Url = targetAddress,
            };

            _targetConnectionCount += numberOfConnections;
            for (int count = 0; count < numberOfConnections; count++)
            {
                var client = new Client();

                client.OnClosed += OnClosed;

                await client.CreateAndStartConnection(connectArguments);
                _clients.Add(client);
            }

            Log("Connections connected succesfully");
        }

        public async Task StartTest(int sendInterval, int sendBytes)
        {
            Log("Worker received start test command with interval {0} and message size {1}.", sendInterval, sendBytes);

            var startTestArguments = new CrankArguments()
            {
                SendInterval = sendInterval,
                SendBytes = sendBytes
            };

            foreach (var client in _clients)
            {
                client.StartTest(startTestArguments);
            }

            Log("Test started succesfully");
        }

        public async Task Stop()
        {
            Log("Worker received stop command");
            _targetConnectionCount = 0;

            while (!_clients.IsEmpty)
            {
                Client client;
                if (_clients.TryTake(out client))
                {
                    client.StopConnection();
                }
            }

            _sendStatusCts.Cancel();
            Log("Connections stopped succesfully");
            _targetConnectionCount = 0;
        }

        private void OnClosed()
        {
            // Indicates that the connection was closed
        }

        private void OnExited(object sender, EventArgs args)
        {
            Environment.Exit(0);
        }

        private void Log(string format, params object[] arguments)
        {
            _agent.Log(_processId, string.Format(format, arguments));
        }

        private async Task SendStatusUpdate(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                int connectedCount = 0, disconnectedCount = 0, reconnectingCount = 0;

                foreach (var client in _clients)
                {
                    switch (client.ConnectionState)
                    {
                        case ConnectionState.Connected:
                            connectedCount++;
                            break;
                        case ConnectionState.Disconnected:
                            disconnectedCount++;
                            break;
                        case ConnectionState.Reconnecting:
                            reconnectingCount++;
                            break;
                    }
                }

                await _agent.Status(
                    _processId,
                    new StatusInformation
                    {
                        ConnectedCount = connectedCount,
                        DisconnectedCount = disconnectedCount,
                        ReconnectingCount = reconnectingCount,
                        TargetConnectionCount = _targetConnectionCount
                    }
                );

                // Sending once per 5 seconds to avoid overloading the Test Controller
                try
                {
                    await Task.Delay(5000, cancellationToken);
                }
                catch (TaskCanceledException)
                {
                }
            }
        }
    }
}
