using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using UnityEngine;

//currently this is telepathy only
namespace Mirror {
    public sealed class NetworkServerSimple {
        Telepathy.Server server = new Telepathy.Server();
        bool s_Active;
        bool s_DontListen;
        bool s_LocalClientActive;
        ULocalConnectionToClient s_LocalConnection;

        Dictionary<short, NetworkMessageDelegate> s_MessageHandlers = new Dictionary<short, NetworkMessageDelegate>();

        // <connectionId, NetworkConnectionCustom>
        Dictionary<int, NetworkConnectionCustom> s_Connections = new Dictionary<int, NetworkConnectionCustom>();

        int s_ServerHostId = -1;
        int s_ServerPort = -1;
        bool s_UseWebSockets;
        bool s_Initialized;

        public int listenPort { get { return s_ServerPort; } }
        public int serverHostId { get { return s_ServerHostId; } }

        public Dictionary<int, NetworkConnectionCustom> connections { get { return s_Connections; } }
        public Dictionary<short, NetworkMessageDelegate> handlers { get { return s_MessageHandlers; } }
        
        public bool dontListen { get { return s_DontListen; } set { s_DontListen = value; } }
        public bool useWebSockets { get { return s_UseWebSockets; } set { s_UseWebSockets = value; } }

        public bool active { get { return s_Active; } }
        public bool localClientActive { get { return s_LocalClientActive; } }

        Type s_NetworkConnectionClass = typeof(NetworkConnectionCustom);
        public Type networkConnectionClass { get { return s_NetworkConnectionClass; } }
        public void SetNetworkConnectionClass<T>() where T : NetworkConnectionCustom {
            s_NetworkConnectionClass = typeof(T);
        }

        public void Reset() {
            s_Active = false;
        }

        public void Shutdown() {
            if (s_Initialized) {
                InternalDisconnectAll();

                if (s_DontListen) {
                    // was never started, so dont stop
                } else {
                    server.Stop();
                    s_ServerHostId = -1;
                }

                s_Initialized = false;
            }
            s_DontListen = false;
            s_Active = false;
        }

        public void Initialize() {
            if (s_Initialized)
                return;

            s_Initialized = true;
            if (LogFilter.Debug) { Debug.Log("NetworkServer Created version " + Version.Current); }
        }

        public bool Listen(int serverPort, int maxConnections) {
            return InternalListen(null, serverPort, maxConnections);
        }

        public bool Listen(string ipAddress, int serverPort, int maxConnections) {
            return InternalListen(ipAddress, serverPort, maxConnections);
        }

        internal bool InternalListen(string ipAddress, int serverPort, int maxConnections) {
            Initialize();

            // only start server if we want to listen
            if (!s_DontListen) {
                s_ServerPort = serverPort;

                server.Start(serverPort, maxConnections);
                s_ServerHostId = 0; // so it doesn't return false


                if (s_ServerHostId == -1) {
                    return false;
                }

                if (LogFilter.Debug) { Debug.Log("Server listen: " + (ipAddress != null ? ipAddress : "") + ":" + s_ServerPort); }
            }
            RegisterHandler(MsgType.Ping, NetworkTime.OnServerPing);
            s_Active = true;
            return true;
        }

        public bool AddConnection(NetworkConnectionCustom conn) {
            if (!s_Connections.ContainsKey(conn.connectionId)) {
                // connection cannot be null here or conn.connectionId
                // would throw NRE
                s_Connections[conn.connectionId] = conn;
                conn.SetHandlers(s_MessageHandlers);
                return true;
            }
            // already a connection with this id
            return false;
        }

        public bool RemoveConnection(int connectionId) {
            return s_Connections.Remove(connectionId);
        }

        internal void RemoveLocalClient(NetworkConnectionCustom localClientConnection) {
            if (s_LocalConnection != null) {
                s_LocalConnection.Disconnect();
                s_LocalConnection.Dispose();
                s_LocalConnection = null;
            }
            s_LocalClientActive = false;
            RemoveConnection(0);
        }

        public bool SendToAll(short msgType, MessageBase msg, int channelId = Channels.DefaultReliable) {
            if (LogFilter.Debug) { Debug.Log("Server.SendToAll id:" + msgType); }

            bool result = true;
            foreach (KeyValuePair<int, NetworkConnectionCustom> kvp in connections) {
                NetworkConnectionCustom conn = kvp.Value;
                result &= conn.Send(msgType, msg, channelId);
            }
            return result;
        }

        public void DisconnectAll() {
            InternalDisconnectAll();
        }

        public void DisconnectAllConnections() {
            foreach (KeyValuePair<int, NetworkConnectionCustom> kvp in connections) {
                NetworkConnectionCustom conn = kvp.Value;
                conn.Disconnect();
                conn.Dispose();
            }
        }

        internal void InternalDisconnectAll() {
            DisconnectAllConnections();

            if (s_LocalConnection != null) {
                s_LocalConnection.Disconnect();
                s_LocalConnection.Dispose();
                s_LocalConnection = null;
            }

            s_Active = false;
            s_LocalClientActive = false;
        }

        // The user should never need to pump the update loop manually
        public void Update() {
            InternalUpdate();
        }

        internal void InternalUpdate() {
            if (s_ServerHostId == -1)
                return;

            int connectionId;
            TransportEvent transportEvent;
            byte[] data;
            while (ServerGetNextMessage(out connectionId, out transportEvent, out data)) {
                switch (transportEvent) {
                    case TransportEvent.Connected:
                        //Debug.Log("NetworkServer loop: Connected");
                        HandleConnect(connectionId, 0);
                        break;
                    case TransportEvent.Data:
                        //Debug.Log("NetworkServer loop: clientId: " + message.connectionId + " Data: " + BitConverter.ToString(message.data));
                        HandleData(connectionId, data, 0);
                        break;
                    case TransportEvent.Disconnected:
                        //Debug.Log("NetworkServer loop: Disconnected");
                        HandleDisconnect(connectionId, 0);
                        break;
                }
            }
            
        }

        public bool ServerGetNextMessage(out int connectionId, out TransportEvent transportEvent, out byte[] data) {
            Telepathy.Message message;
            if (server.GetNextMessage(out message)) {
                // convert Telepathy EventType to TransportEvent
                if (message.eventType == Telepathy.EventType.Connected)
                    transportEvent = TransportEvent.Connected;
                else if (message.eventType == Telepathy.EventType.Data)
                    transportEvent = TransportEvent.Data;
                else if (message.eventType == Telepathy.EventType.Disconnected)
                    transportEvent = TransportEvent.Disconnected;
                else
                    transportEvent = TransportEvent.Disconnected;

                // assign rest of the values and return true
                connectionId = message.connectionId;
                data = message.data;
                return true;
            }

            connectionId = -1;
            transportEvent = TransportEvent.Disconnected;
            data = null;
            return false;
        }

        void HandleConnect(int connectionId, byte error) {
            if (LogFilter.Debug) { Debug.Log("Server accepted client:" + connectionId); }

            if (error != 0) {
                GenerateConnectError(error);
                return;
            }

            // get ip address from connection
            string address;
            server.GetConnectionInfo(connectionId, out address);

            // add player info
            NetworkConnectionCustom conn = (NetworkConnectionCustom)Activator.CreateInstance(s_NetworkConnectionClass);
            conn.server = server;
            conn.Initialize(address, s_ServerHostId, connectionId);
            AddConnection(conn);
            OnConnected(conn);
        }

        void OnConnected(NetworkConnectionCustom conn) {
            if (LogFilter.Debug) { Debug.Log("Server accepted client:" + conn.connectionId); }
            conn.InvokeHandlerNoData((short)MsgType.Connect);
        }

        void HandleDisconnect(int connectionId, byte error) {
            if (LogFilter.Debug) { Debug.Log("Server disconnect client:" + connectionId); }

            NetworkConnectionCustom conn;
            if (s_Connections.TryGetValue(connectionId, out conn)) {
                conn.Disconnect();
                RemoveConnection(connectionId);
                if (LogFilter.Debug) { Debug.Log("Server lost client:" + connectionId); }

                OnDisconnected(conn);
            }
        }

        void OnDisconnected(NetworkConnectionCustom conn) {
            conn.InvokeHandlerNoData((short)MsgType.Disconnect);

            if (conn.playerController != null) {
                //NOTE: should there be default behaviour here to destroy the associated player?
                Debug.LogWarning("Player not destroyed when connection disconnected.");
            }

            if (LogFilter.Debug) { Debug.Log("Server lost client:" + conn.connectionId); }
            conn.RemoveObservers();
            conn.Dispose();
        }

        void HandleData(int connectionId, byte[] data, byte error) {
            NetworkConnectionCustom conn;
            if (s_Connections.TryGetValue(connectionId, out conn)) {
                OnData(conn, data);
            } else {
                Debug.LogError("HandleData Unknown connectionId:" + connectionId);
            }
        }

        void OnData(NetworkConnectionCustom conn, byte[] data) {
            conn.TransportReceive(data);
        }

        void GenerateConnectError(byte error) {
            Debug.LogError("UNet Server Connect Error: " + error);
            GenerateError(null, error);
        }

        void GenerateError(NetworkConnectionCustom conn, byte error) {
            if (handlers.ContainsKey((short)MsgType.Error)) {
                ErrorMessage msg = new ErrorMessage();
                msg.errorCode = error;

                // write the message to a local buffer
                NetworkWriter writer = new NetworkWriter();
                msg.Serialize(writer);

                // pass a reader (attached to local buffer) to handler
                NetworkReader reader = new NetworkReader(writer.ToArray());
                conn.InvokeHandler((short)MsgType.Error, reader);
            }
        }

        public void RegisterHandler(short msgType, NetworkMessageDelegate handler) {
            if (s_MessageHandlers.ContainsKey(msgType)) {
                if (LogFilter.Debug) { Debug.Log("NetworkServer.RegisterHandler replacing " + msgType); }
            }
            s_MessageHandlers[msgType] = handler;
        }

        public void RegisterHandler(MsgType msgType, NetworkMessageDelegate handler) {
            RegisterHandler((short)msgType, handler);
        }

        public void UnregisterHandler(short msgType) {
            s_MessageHandlers.Remove(msgType);
        }

        public void UnregisterHandler(MsgType msgType) {
            UnregisterHandler((short)msgType);
        }

        public void ClearHandlers() {
            s_MessageHandlers.Clear();
        }

        public void SendToClient(int connectionId, short msgType, MessageBase msg) {
            NetworkConnectionCustom conn;
            if (connections.TryGetValue(connectionId, out conn)) {
                conn.Send(msgType, msg);
                return;
            }
            Debug.LogError("Failed to send message to connection ID '" + connectionId + ", not found in connection list");
        }

        internal bool InvokeBytes(ULocalConnectionToServer conn, byte[] buffer) {
            ushort msgType;
            byte[] content;
            if (Protocol.UnpackMessage(buffer, out msgType, out content)) {
                if (handlers.ContainsKey((short)msgType) && s_LocalConnection != null) {
                    // this must be invoked with the connection to the client, not the client's connection to the server
                    s_LocalConnection.InvokeHandler((short)msgType, new NetworkReader(content));
                    return true;
                }
            }
            Debug.LogError("InvokeBytes: failed to unpack message:" + BitConverter.ToString(buffer));
            return false;
        }
    }
}
