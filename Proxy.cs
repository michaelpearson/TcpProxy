using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace TcpProxy
{
    public class Proxy
    {
        private const int Mtu = 1500;
        
        private readonly int _listeningPort;
        private readonly IPEndPoint _remoteEndpoint;
        
        private Socket _serverSocket;
        private Thread _proxyThread;

        public Proxy(int listeningPort, string destAddress, int destPort)
        {
            _listeningPort = listeningPort;

            var resolvedAddress = Dns.GetHostAddresses(destAddress);
            if (resolvedAddress.Length == 0)
            {
                throw new Exception("Could not resolve address");
            }
            _remoteEndpoint = new IPEndPoint(resolvedAddress[0], destPort);
        }

        public void Begin(CancellationToken cancellationToken)
        {
            _proxyThread = new Thread(() => BeginListening(cancellationToken));
            _proxyThread.Start();
        }

        private void BeginListening(CancellationToken cancellationToken)
        {
            _serverSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            _serverSocket.Bind(new IPEndPoint(IPAddress.Loopback, _listeningPort));

            Console.WriteLine("Listening");
            _serverSocket.Listen(1);

            Socket remoteConnection = null, localConnection = null;
            while (!cancellationToken.IsCancellationRequested)
            {
                if (!_serverSocket.Poll(1000 * 500, SelectMode.SelectRead))
                {
                    continue;
                }
                Console.WriteLine("Connection ready, opening remote connection");
                remoteConnection = OpenRemoteConnection();
                if (!NegotiateProxyConnection(remoteConnection, cancellationToken))
                {
                    break;
                }
                Console.WriteLine("Got remote connection");
                localConnection = _serverSocket.Accept();
                Console.WriteLine("Got local connection. Begining proxy.");
                ProxyConnection(remoteConnection, localConnection);
            }

            remoteConnection?.Close();
            localConnection?.Close();
            _serverSocket.Close();
        }

        private bool NegotiateProxyConnection(Socket connectionToProxy, CancellationToken cancellationToken)
        {
            // This is unsafe :/, need to that we are able to send & that the data actually got sent
            connectionToProxy.Send(Encoding.ASCII.GetBytes($"CONNECT {_remoteEndpoint.Address}:{_remoteEndpoint.Port} HTTP/1.1\r\n\r\n"));

            var concurrentNewLines = 0;
            while (!cancellationToken.IsCancellationRequested)
            {
                if (!connectionToProxy.Poll(500 * 1000, SelectMode.SelectRead))
                {
                    continue;
                }

                if (connectionToProxy.Available == 0)
                {
                    connectionToProxy.Close();
                    return false;
                }
                
                var buffer = new byte[1];
                connectionToProxy.Receive(buffer);

                switch ((char)buffer[0])
                {
                    case '\n':
                        concurrentNewLines++;
                        break;
                    case '\r':
                        break;
                    default:
                        concurrentNewLines = 0;
                        break;
                }

                if (concurrentNewLines >= 2)
                {
                    return true;
                }
            }
            return false;
        }

        private Socket OpenRemoteConnection()
        {
            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            socket.Connect(_remoteEndpoint);
            return socket;
        }

        private void ProxyConnection(Socket remoteConnection, Socket localConnection)
        {
            try
            {
                while (remoteConnection.Connected && localConnection.Connected)
                {
                    var read = new List<Socket> {remoteConnection, localConnection};
                    var write = new List<Socket>();
                    var error = new List<Socket> {remoteConnection, localConnection};
                    Socket.Select(read, write, error, 1000 * 500);
                    Console.WriteLine($"Select finished. Read: {read.Count}, Error: {error.Count}");

                    if (error.Count != 0)
                    {
                        remoteConnection.Close();
                        localConnection.Close();
                        throw new Exception("Socket error!");
                    }

                    if (read.Any(s => s.Available == 0))
                    {
                        localConnection.Close();
                        remoteConnection.Close();
                        Console.WriteLine("Connection closed");
                        return;
                    }

                    if (read.Contains(remoteConnection))
                    {
                        ProxyChunk(remoteConnection, localConnection);
                    }

                    if (read.Contains(localConnection))
                    {
                        ProxyChunk(localConnection, remoteConnection);
                    }
                }
            }
            catch (SocketException socketException)
            {
                Console.Error.Write(socketException.Message);
            }
            finally
            {
                localConnection.Close();
                remoteConnection.Close();
            }
        }

        private void ProxyChunk(Socket read, Socket write)
        {
            var buffer = new byte[Mtu];
            var receivedLength = read.Receive(buffer);
            if (receivedLength <= 0)
            {
                return;
            }

            var position = 0;
            while (position < receivedLength)
            {
                if (!write.Poll(1000 * 500, SelectMode.SelectWrite))
                {
                    continue;
                }
                position += write.Send(buffer, position, receivedLength, SocketFlags.None);
            }
        }
    }
}