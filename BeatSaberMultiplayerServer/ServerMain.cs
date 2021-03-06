﻿using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using BeatSaberMultiplayerServer.Misc;
using ServerCommons.Data;
using ServerCommons.Misc;
using Settings = BeatSaberMultiplayerServer.Misc.Settings;

namespace BeatSaberMultiplayerServer {
    class ServerMain {
        static TcpListener _listener;

        public static List<Client> clients = new List<Client>();

        public static ServerState serverState = ServerState.Lobby;
        public static List<CustomSongInfo> availableSongs = new List<CustomSongInfo>();

        public static int currentSongIndex = -1;
        private static int lastSelectedSong = -1;

        public static TimeSpan playTime = new TimeSpan();
        
        private static TcpClient _serverHubClient;

        public int ID { get; set; }

        static void Main(string[] args) => new ServerMain().Start(args);

        public void Start(string[] args) {
            Logger.Instance.Log("Beat Saber Multiplayer Server v0.1");

            Logger.Instance.Log($"Hosting Server @ {Settings.Instance.Server.IP}");

            Logger.Instance.Log("Downloading songs from BeatSaver...");
            DownloadSongs();


            Logger.Instance.Log("Starting server...");
            _listener = new TcpListener(IPAddress.Any, Settings.Instance.Server.Port);

            _listener.Start();

            Logger.Instance.Log("Waiting for clients...");

            Thread _listenerThread = new Thread(AcceptClientThread);
            _listenerThread.Start();

            Thread _serverStateThread = new Thread(ServerStateControllerThread);
            _serverStateThread.Start();
            
            try
            {
                ConnectToServerHub(Settings.Instance.Server.ServerHubIP, Settings.Instance.Server.ServerHubPort);
            }catch(Exception e)
            {
                Logger.Instance.Error($"Can't connect to ServerHub! Exception: {e.Message}");
            }
            
            ShutdownEventCatcher.Shutdown += ServerShutdown;
        }        

        public void ConnectToServerHub(string serverHubIP, int serverHubPort)
        {
            _serverHubClient = new TcpClient(serverHubIP, serverHubPort);

            ServerDataPacket packet = new ServerDataPacket {
                ConnectionType = ConnectionType.Server,
                IPv4 = Settings.Instance.Server.IP,
                Port = Settings.Instance.Server.Port,
                Name = Settings.Instance.Server.ServerName
            };

            byte[] packetBytes = packet.ToBytes();
            
            _serverHubClient.GetStream().Write(packetBytes, 0, packetBytes.Length);
            
            byte[] bytes = new byte[Packet.MAX_BYTE_LENGTH];
            if (_serverHubClient.GetStream().Read(bytes, 0, bytes.Length) != 0) {
                packet = (ServerDataPacket)Packet.ToPacket(bytes);
            }

            ID = packet.ID;

            Logger.Instance.Log($"The ID of this server is {ID}");
            
        }

        private static void DownloadSongs() {

            Settings.Instance.Server.Downloaded.GetDirectories().AsParallel().ForAll(dir => dir.Delete(true));

            Settings.Instance.AvailableSongs.Songs.AsParallel().ForAll(id => {
                var zipPath = Path.Combine(Settings.Instance.Server.Downloads.FullName, $"{id}.zip");
                Thread.Sleep(25);
                using (var client = new WebClient()) {
                    client.Headers.Add("user-agent",
                        $"BeatSaverMultiplayerServer-{Assembly.GetEntryAssembly().GetName().Version}");
                    if (Settings.Instance.Server.Downloads.GetFiles().All(o => o.Name != $"{id}.zip")) {
                        Logger.Instance.Log($"Downloading {id}.zip");
                        client.DownloadFile($"https://beatsaver.com/dl.php?id={id}", zipPath);
                    }
                }

                ZipArchive zip = null;
                try {
                    zip = ZipFile.OpenRead(zipPath);
                }
                catch (Exception ex) {
                    Logger.Instance.Exception(ex.Message);
                }

                var songName = zip?.Entries[0].FullName.Split('/')[0];
                try {
                    zip?.ExtractToDirectory(Settings.Instance.Server.Downloaded.FullName);
                    try {
                        zip?.Dispose();
                    }
                    catch (IOException ex) {
                        Logger.Instance.Exception($"Failed to remove Zip [{id}]");
                    }
                }
                catch (IOException ex) {
                    Logger.Instance.Exception($"Folder [{songName}] exists. Continuing.");
                    try {
                        zip.Dispose();
                    }
                    catch (IOException) {
                        Logger.Instance.Exception($"Failed to remove Zip [{id}]");
                    }
                }
            });

            Logger.Instance.Log("All songs downloaded!");

            List<CustomSongInfo> _songs = SongLoader.RetrieveAllSongs();
            
            _songs.AsParallel().ForAll(song => {
                Logger.Instance.Log($"Processing {song.songName} {song.songSubName}");
                using (NVorbis.VorbisReader vorbis =
                    new NVorbis.VorbisReader($"{song.path}/{song.difficultyLevels[0].audioPath}")) {
                    song.duration = vorbis.TotalTime;
                }

                availableSongs.Add(song);
            });

            Logger.Instance.Log("Done!");
        }

        static void ServerStateControllerThread() {
            Stopwatch _timer = new Stopwatch();
            _timer.Start();
            int _timerSeconds = 0;
            TimeSpan _lastTime = new TimeSpan();

            float lobbyTimer = 0;
            float sendTimer = 0;

            int lobbyTime = Settings.Instance.Server.LobbyTime;

            TimeSpan deltaTime;

            while (true) {
                deltaTime = (_timer.Elapsed - _lastTime);

                _lastTime = _timer.Elapsed;

                switch (serverState) {
                    case ServerState.Lobby: {
                        lobbyTimer += (float) deltaTime.TotalSeconds;

                        if (clients.Count == 0) {
                            lobbyTimer = 0;
                        }

                        if ((int) Math.Ceiling(lobbyTimer) > _timerSeconds && _timerSeconds > -1) {
                            _timerSeconds = (int) Math.Ceiling(lobbyTimer);
                            SendToAllClients(JsonConvert.SerializeObject(
                                new ServerCommand(ServerCommandType.SetLobbyTimer,
                                    Math.Max(lobbyTime - _timerSeconds, 0))));
                        }


                        if (lobbyTimer >= lobbyTime / 2 && currentSongIndex == -1) {
                            currentSongIndex = lastSelectedSong;
                            currentSongIndex++;
                            if (currentSongIndex >= availableSongs.Count) {
                                currentSongIndex = 0;
                            }

                            SendToAllClients(JsonConvert.SerializeObject(new ServerCommand(
                                ServerCommandType.SetSelectedSong,
                                _selectedLevelID: availableSongs[currentSongIndex].levelId,
                                _difficulty: GetPreferredDifficulty(availableSongs[currentSongIndex]))));
                        }

                        if (lobbyTimer >= lobbyTime) {
                            SendToAllClients(JsonConvert.SerializeObject(new ServerCommand(
                                ServerCommandType.SetSelectedSong,
                                _selectedLevelID: availableSongs[currentSongIndex].levelId,
                                _difficulty: GetPreferredDifficulty(availableSongs[currentSongIndex]))));
                            SendToAllClients(
                                JsonConvert.SerializeObject(
                                    new ServerCommand(ServerCommandType.StartSelectedSongLevel)));

                            serverState = ServerState.Playing;
                            Logger.Instance.Log("Starting song " + availableSongs[currentSongIndex].songName + " " +
                                                availableSongs[currentSongIndex].songSubName + "...");
                            _timerSeconds = 0;
                            lobbyTimer = 0;
                        }
                    }
                        ;
                        break;
                    case ServerState.Playing: {
                        sendTimer += (float) deltaTime.TotalSeconds;
                        playTime += deltaTime;

                        if (sendTimer >= 1f) {
                            SendToAllClients(JsonConvert.SerializeObject(new ServerCommand(
                                ServerCommandType.SetPlayerInfos,
                                _playerInfos: (clients.Where(x => x.playerInfo != null)
                                    .OrderByDescending(x => x.playerInfo.playerScore)
                                    .Select(x => JsonConvert.SerializeObject(x.playerInfo))).ToArray())));
                            sendTimer = 0f;
                        }

                        if (playTime.TotalSeconds >= availableSongs[currentSongIndex].duration.TotalSeconds + 5f) {
                            playTime = new TimeSpan();
                            sendTimer = 0f;
                            serverState = ServerState.Lobby;
                            lastSelectedSong = currentSongIndex;
                            currentSongIndex = -1;
                            Logger.Instance.Log("Returning to lobby...");
                        }

                        if (clients.Count == 0 && playTime.TotalSeconds > 5) {
                            playTime = new TimeSpan();
                            sendTimer = 0f;
                            serverState = ServerState.Lobby;
                            lastSelectedSong = currentSongIndex;
                            currentSongIndex = -1;

                            Logger.Instance.Log("Returning to lobby(NO PLAYERS)...");
                        }
                    }
                        ;
                        break;
                }


                Thread.Sleep(2);
            }
        }

        static int GetPreferredDifficulty(CustomSongInfo _song) {
            int difficulty = 0;

            foreach (CustomSongInfo.DifficultyLevel diff in _song.difficultyLevels) {
                if ((int) Enum.Parse(typeof(Difficulty), diff.difficulty) <= 3 &&
                    (int) Enum.Parse(typeof(Difficulty), diff.difficulty) >= difficulty) {
                    difficulty = (int) Enum.Parse(typeof(Difficulty), diff.difficulty);
                }
            }

            if (difficulty == 0 && _song.difficultyLevels.Length > 0)
            {
                difficulty = (int)Enum.Parse(typeof(Difficulty), _song.difficultyLevels[0].difficulty);
            }

            return difficulty;
        }

        static void SendToAllClients(string message, bool retryOnError = false) {
            try {
                for (int i = 0; i < clients.Count; i++) {
                    if (clients[i] != null)
                        clients[i].SendToClient(message);
                }
            }
            catch (Exception e) {
                Logger.Instance.Exception("Can't send message to all clients! Exception: " + e);
            }
        }

        static void AcceptClientThread() {
            while (true) {
                Thread _thread = new Thread(new ParameterizedThreadStart(ClientThread));

                _thread.Start(_listener.AcceptTcpClient());
            }
        }

        static void ClientThread(Object stateInfo) {
            clients.Add(new Client((TcpClient) stateInfo));
        }
        
        private void ServerShutdown(ShutdownEventArgs args)
        {
            Logger.Instance.Log("Shutting down server...");
            if(_serverHubClient != null && _serverHubClient.Connected)
            {
                ServerDataPacket packet = new ServerDataPacket
                {
                    ConnectionType = ConnectionType.Server,
                    IPv4 = Settings.Instance.Server.IP,
                    Port = Settings.Instance.Server.Port,
                    Name = Settings.Instance.Server.ServerName,
                    RemoveFromCollection = true
                };

                byte[] packetBytes = packet.ToBytes();

                _serverHubClient.GetStream().Write(packetBytes, 0, packetBytes.Length);
                Logger.Instance.Log("Removed this server from ServerHub");

                _serverHubClient.Close();
            }
        }

    }
}