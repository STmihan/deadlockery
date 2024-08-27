﻿using ProtoBuf;
using SteamKit2;
using SteamKit2.Authentication;
using SteamKit2.GC;
using SteamKit2.GC.Deadlock.Internal;
using SteamKit2.Internal;

namespace DeadlockAPI
{
    public class DeadlockClient
    {
        SteamClient client;

        SteamUser user;
        SteamGameCoordinator gameCoordinator;

        CallbackManager callbackMgr;

        string userName;
        string password;
        string previouslyStoredGuardData;

        const int APPID = 1422450;
        
        bool _isInitialized = false;

        public DeadlockClient(string userName, string password)
        {
            this.userName = userName;
            this.password = password;

            client = new SteamClient();

            user = client.GetHandler<SteamUser>();
            gameCoordinator = client.GetHandler<SteamGameCoordinator>();

            callbackMgr = new CallbackManager(client);
            callbackMgr.Subscribe<SteamClient.ConnectedCallback>(OnConnected);
            callbackMgr.Subscribe<SteamClient.DisconnectedCallback>(OnDisconnected);
            callbackMgr.Subscribe<SteamUser.LoggedOnCallback>(OnLoggedOn);
            callbackMgr.Subscribe<SteamGameCoordinator.MessageCallback>(OnGCMessage);

            previouslyStoredGuardData = File.Exists("guard.txt") ? File.ReadAllText("guard.txt") : string.Empty;
        }

        public bool IsConnected { get => client.IsConnected; }

        public void Connect()
        {
            Console.WriteLine("Connecting to Steam");
            client.Connect();
        }

        public void Disconnect()
        {
            client.Disconnect();
        }

        public void Wait()
        {
            while (true)
            {
                callbackMgr.RunWaitCallbacks(TimeSpan.FromSeconds(1));
            }
        }

        public void RunCallbacks(TimeSpan t)
        {
            callbackMgr.RunWaitCallbacks(t);
        }

        async void OnConnected(SteamClient.ConnectedCallback callback)
        {
            Console.WriteLine("Connected! Logging into Steam as {0}", userName);

            var authSession = await client.Authentication.BeginAuthSessionViaCredentialsAsync(new AuthSessionDetails
            {
                Username = userName,
                Password = password,
                IsPersistentSession = true,
                GuardData = previouslyStoredGuardData,
                Authenticator = new UserConsoleAuthenticator(),
            });
            var pollResponse = await authSession.PollingWaitForResultAsync();
            if (pollResponse.NewGuardData != null)
            {
                previouslyStoredGuardData = pollResponse.NewGuardData;
                File.WriteAllText("guard.txt", previouslyStoredGuardData);
            }
            user.LogOn(new SteamUser.LogOnDetails
            {
                Username = pollResponse.AccountName,
                AccessToken = pollResponse.RefreshToken,
                ShouldRememberPassword = true,
            });
        }

        void OnDisconnected(SteamClient.DisconnectedCallback callback)
        {
            Console.WriteLine("Disconnected :(\nTrying again in 10s");
            Thread.Sleep(10000);
            Connect();
        }

        void OnLoggedOn(SteamUser.LoggedOnCallback callback)
        {
            if (callback.Result != EResult.OK)
            {
                Console.WriteLine("Unable to log on to Steam: {0}", callback.Result);
                return;
            }

            Console.WriteLine("Logged in! Launching Deadlock");

            var playGame = new ClientMsgProtobuf<CMsgClientGamesPlayed>(EMsg.ClientGamesPlayed);
            playGame.Body.games_played.Add(new CMsgClientGamesPlayed.GamePlayed
            {
                game_id = new GameID(APPID)
            });
            
            client.Send(playGame);
            
            const int timeout = 10;
            for (int i = 0; i < timeout; i++)
            {
                Console.WriteLine("Waiting for GC to be ready..." + (timeout - i));
                Thread.Sleep(1000);
            }
            Console.WriteLine("Sending ClientHello");
            var clientHello = new ClientGCMsgProtobuf<CMsgCitadelClientHello>((uint)EGCBaseClientMsg.k_EMsgGCClientHello);
            client.GetHandler<SteamGameCoordinator>()?.Send(clientHello, APPID);
        }

        void OnGCMessage(SteamGameCoordinator.MessageCallback callback)
        {
            var messageMap = new Dictionary<uint, Action<IPacketGCMsg>>
            {
                { (uint) EGCBaseClientMsg.k_EMsgGCClientWelcome, OnClientWelcome },
                { (uint) EGCCitadelClientMessages.k_EMsgGCToClientDevPlaytestStatus, OnDevPlaytestStatus },
            };

            Action<IPacketGCMsg> func;
            if (!messageMap.TryGetValue(callback.EMsg, out func))
            {
                return;
            }

            func(callback.Message);
        }

        public async Task<U> SendAndReceiveWithJob<T, U>(ClientGCMsgProtobuf<T> msg)
            where T : IExtensible, new()
            where U : IExtensible, new()
        {
            msg.SourceJobID = client.GetNextJobID();
            gameCoordinator.Send(msg, APPID);
            var cb = await new AsyncJob<SteamGameCoordinator.MessageCallback>(client, msg.SourceJobID);
            var response = new ClientGCMsgProtobuf<U>(cb.Message);
            return response.Body;
        }

        public class MatchMetaData
        {
            public required CMsgClientToGCGetMatchMetaDataResponse Data;
            public required string ReplayURL;
            public required string MetadataURL;
        }

        public async Task<MatchMetaData?> GetMatchMetaData(uint matchId)
        {
            var msg = new ClientGCMsgProtobuf<CMsgClientToGCGetMatchMetaData>((uint)EGCCitadelClientMessages.k_EMsgClientToGCGetMatchMetaData);
            msg.Body.match_id = matchId;
            var r = await SendAndReceiveWithJob<CMsgClientToGCGetMatchMetaData, CMsgClientToGCGetMatchMetaDataResponse>(msg);
            if (r == null) return null;
            return new MatchMetaData()
            {
                Data = r,
                ReplayURL = $"http://replay{r.cluster_id}.valve.net/{APPID}/{matchId}_{r.replay_salt}.dem.bz2",
                MetadataURL = $"http://replay{r.cluster_id}.valve.net/{APPID}/{matchId}_{r.metadata_salt}.meta.bz2"
            };
        }

        public async Task<CMsgClientToGCGetGlobalMatchHistoryResponse?> GetGlobalMatchHistory(uint cursor = 0)
        {
            var msg = new ClientGCMsgProtobuf<CMsgClientToGCGetGlobalMatchHistory>((uint)EGCCitadelClientMessages.k_EMsgClientToGCGetGlobalMatchHistory);
            msg.Body.cursor = cursor;
            return await SendAndReceiveWithJob<CMsgClientToGCGetGlobalMatchHistory, CMsgClientToGCGetGlobalMatchHistoryResponse>(msg);
        }

        public class ClientWelcomeEventArgs : EventArgs
        {
            public required CMsgClientWelcome Data;
        }
        public event EventHandler<ClientWelcomeEventArgs> ClientWelcomeEvent;
        void OnClientWelcome(IPacketGCMsg packetMsg)
        {
            _isInitialized = true;
            var msg = new ClientGCMsgProtobuf<CMsgClientWelcome>(packetMsg);
            ClientWelcomeEvent?.Invoke(this, new ClientWelcomeEventArgs() { Data = msg.Body });
        }

        public class DevPlaytestStatusEventArgs : EventArgs
        {
            public required CMsgGCToClientDevPlaytestStatus Data;
        }
        public event EventHandler<DevPlaytestStatusEventArgs> DevPlaytestStatusEvent;
        void OnDevPlaytestStatus(IPacketGCMsg packetMsg)
        {
            var msg = new ClientGCMsgProtobuf<CMsgGCToClientDevPlaytestStatus>(packetMsg);
            DevPlaytestStatusEvent?.Invoke(this, new DevPlaytestStatusEventArgs() { Data = msg.Body });
        }
    }
}
