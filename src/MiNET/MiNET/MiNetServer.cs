#region LICENSE

// The contents of this file are subject to the Common Public Attribution
// License Version 1.0. (the "License"); you may not use this file except in
// compliance with the License. You may obtain a copy of the License at
// https://github.com/NiclasOlofsson/MiNET/blob/master/LICENSE. 
// The License is based on the Mozilla Public License Version 1.1, but Sections 14 
// and 15 have been added to cover use of software over a computer network and 
// provide for limited attribution for the Original Developer. In addition, Exhibit A has 
// been modified to be consistent with Exhibit B.
// 
// Software distributed under the License is distributed on an "AS IS" basis,
// WITHOUT WARRANTY OF ANY KIND, either express or implied. See the License for
// the specific language governing rights and limitations under the License.
// 
// The Original Code is Niclas Olofsson.
// 
// The Original Developer is the Initial Developer.  The Initial Developer of
// the Original Code is Niclas Olofsson.
// 
// All portions of the code written by Niclas Olofsson are Copyright (c) 2014-2017 Niclas Olofsson. 
// All Rights Reserved.

#endregion

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using log4net;
using Microsoft.IO;
using MiNET.Net;
using MiNET.Plugins;
using MiNET.Utils;
using Newtonsoft.Json;

namespace MiNET
{
	public class MiNetServer
	{
		private static readonly ILog Log = LogManager.GetLogger(typeof (MiNetServer));

		private const int DefaultPort = 19132;

		public IPEndPoint Endpoint { get; private set; }
		private UdpClient _listener;
		private ConcurrentDictionary<IPEndPoint, PlayerNetworkSession> _playerSessions = new ConcurrentDictionary<IPEndPoint, PlayerNetworkSession>();

		public MotdProvider MotdProvider { get; set; }

		public static RecyclableMemoryStreamManager MemoryStreamManager { get; set; } = new RecyclableMemoryStreamManager();

		public IServerManager ServerManager { get; set; }
		public LevelManager LevelManager { get; set; }
		public PlayerFactory PlayerFactory { get; set; }
		public GreylistManager GreylistManager { get; set; }

		public PluginManager PluginManager { get; set; }
		public SessionManager SessionManager { get; set; }

		private Timer _internalPingTimer;
		private Timer _cleanerTimer;

		public int InacvitityTimeout { get; private set; }

		public ServerInfo ServerInfo { get; set; }

		public ServerRole ServerRole { get; set; } = ServerRole.Full;

		public bool ForceOrderingForAll { get; set; }

		internal static DedicatedThreadPool FastThreadPool { get; set; }
		internal static DedicatedThreadPool LevelThreadPool { get; set; }

		public MiNetServer()
		{
			ServerRole = Config.GetProperty("ServerRole", ServerRole.Full);
			InacvitityTimeout = Config.GetProperty("InactivityTimeout", 8500);
			ForceOrderingForAll = Config.GetProperty("ForceOrderingForAll", false);

			int confMinWorkerThreads = Config.GetProperty("MinWorkerThreads", -1);
			int confMinCompletionPortThreads = Config.GetProperty("MinCompletionPortThreads", -1);

			int threads;
			int iothreads;
			ThreadPool.GetMinThreads(out threads, out iothreads);

			//if (confMinWorkerThreads != -1) threads = confMinWorkerThreads;
			//else threads *= 4;

			//if (confMinCompletionPortThreads != -1) iothreads = confMinCompletionPortThreads;
			//else iothreads *= 4;

			//ThreadPool.SetMinThreads(threads, iothreads);
			FastThreadPool = new DedicatedThreadPool(new DedicatedThreadPoolSettings(Environment.ProcessorCount));
			LevelThreadPool = new DedicatedThreadPool(new DedicatedThreadPoolSettings(Environment.ProcessorCount));
			_receiveThreadPool = new DedicatedThreadPool(new DedicatedThreadPoolSettings(Environment.ProcessorCount));
		}

		public MiNetServer(IPEndPoint endpoint) : base()
		{
			Endpoint = endpoint;
		}

		public static bool IsRunningOnMono()
		{
			return Type.GetType("Mono.Runtime") != null;
		}

		public static void DisplayTimerProperties()
		{
			// Display the timer frequency and resolution.
			if (Stopwatch.IsHighResolution)
			{
				Console.WriteLine("Operations timed using the system's high-resolution performance counter.");
			}
			else
			{
				Console.WriteLine("Operations timed using the DateTime class.");
			}

			long frequency = Stopwatch.Frequency;
			Console.WriteLine("  Timer frequency in ticks per second = {0}",
				frequency);
			long nanosecPerTick = (1000L*1000L*1000L)/frequency;
			Console.WriteLine("  Timer is accurate within {0} nanoseconds",
				nanosecPerTick);
		}

		public bool StartServer()
		{
			DisplayTimerProperties();

			if (_listener != null) return false; // Already started

			try
			{
				Log.Info("Initializing...");

				if (ServerRole == ServerRole.Full || ServerRole == ServerRole.Proxy)
				{
					if (Endpoint == null)
					{
						var ip = IPAddress.Parse(Config.GetProperty("ip", "0.0.0.0"));
						int port = Config.GetProperty("port", 19132);
						Endpoint = new IPEndPoint(ip, port);
					}
				}

				ServerManager = ServerManager ?? new DefaultServerManager(this);

				if (ServerRole == ServerRole.Full || ServerRole == ServerRole.Node)
				{
					Log.Info("Loading plugins...");
					PluginManager = new PluginManager();
					PluginManager.LoadPlugins();
					Log.Info("Plugins loaded!");

					// Bootstrap server
					PluginManager.ExecuteStartup(this);

					GreylistManager = GreylistManager ?? new GreylistManager(this);
					SessionManager = SessionManager ?? new SessionManager();
					LevelManager = LevelManager ?? new LevelManager();
					PlayerFactory = PlayerFactory ?? new PlayerFactory();

					PluginManager.EnablePlugins(this, LevelManager);

					// Cache - remove
					LevelManager.GetLevel(null, "Default");
				}

				GreylistManager = GreylistManager ?? new GreylistManager(this);
				MotdProvider = MotdProvider ?? new MotdProvider();

				if (ServerRole == ServerRole.Full || ServerRole == ServerRole.Proxy)
				{
					_listener = CreateListener();

					new Thread(ProcessDatagrams) {IsBackground = true}.Start(_listener);
				}

				ServerInfo = new ServerInfo(LevelManager, _playerSessions)
				{
					MaxNumberOfPlayers = Config.GetProperty("MaxNumberOfPlayers", 1000)
				};
				ServerInfo.MaxNumberOfConcurrentConnects = Config.GetProperty("MaxNumberOfConcurrentConnects", ServerInfo.MaxNumberOfPlayers);

				Log.Info("Server open for business on port " + Endpoint?.Port + " ...");

				return true;
			}
			catch (Exception e)
			{
				Log.Error("Error during startup!", e);
				StopServer();
			}

			return false;
		}

		private UdpClient CreateListener()
		{
			var listener = new UdpClient(Endpoint);

			if (IsRunningOnMono())
			{
				listener.Client.ReceiveBufferSize = 1024*1024*3;
				listener.Client.SendBufferSize = 4096;
			}
			else
			{
				//_listener.Client.ReceiveBufferSize = 1600*40000;
				listener.Client.ReceiveBufferSize = int.MaxValue;
				//_listener.Client.SendBufferSize = 1600*40000;
				listener.Client.SendBufferSize = int.MaxValue;
				listener.DontFragment = false;
				listener.EnableBroadcast = false;

				// SIO_UDP_CONNRESET (opcode setting: I, T==3)
				// Windows:  Controls whether UDP PORT_UNREACHABLE messages are reported.
				// - Set to TRUE to enable reporting.
				// - Set to FALSE to disable reporting.

				uint IOC_IN = 0x80000000;
				uint IOC_VENDOR = 0x18000000;
				uint SIO_UDP_CONNRESET = IOC_IN | IOC_VENDOR | 12;
				listener.Client.IOControl((int) SIO_UDP_CONNRESET, new byte[] {Convert.ToByte(false)}, null);

				//
				//WARNING: We need to catch errors here to remove the code above.
				//
			}

			//_cleanerTimer = new Timer(Update, null, 10, Timeout.Infinite);
			return listener;
		}

		public bool StopServer()
		{
			try
			{
				Log.Info("Disabling plugins...");
				PluginManager.DisablePlugins();

				Log.Info("Shutting down...");
				if (_listener == null) return true; // Already stopped. It's ok.

				_listener.Close();
				_listener = null;

				return true;
			}
			catch (Exception e)
			{
				Log.Error(e);
			}

			return false;
		}

		private void ProcessDatagrams(object state)
		{
			UdpClient listener = (UdpClient) state;

			while (true)
			{
				// Check if we already closed the server
				if (listener.Client == null) return;

				// WSAECONNRESET:
				// The virtual circuit was reset by the remote side executing a hard or abortive close. 
				// The application should close the socket; it is no longer usable. On a UDP-datagram socket 
				// this error indicates a previous send operation resulted in an ICMP Port Unreachable message.
				// Note the spocket settings on creation of the server. It makes us ignore these resets.
				IPEndPoint senderEndpoint = null;
				Byte[] receiveBytes = null;
				try
				{
					//var result = listener.ReceiveAsync().Result;
					//senderEndpoint = result.RemoteEndPoint;
					//receiveBytes = result.Buffer;
					receiveBytes = listener.Receive(ref senderEndpoint);

					Interlocked.Exchange(ref ServerInfo.AvailableBytes, listener.Available);
					Interlocked.Increment(ref ServerInfo.NumberOfPacketsInPerSecond);
					Interlocked.Add(ref ServerInfo.TotalPacketSizeIn, receiveBytes.Length);

					if (receiveBytes.Length != 0)
					{
						_receiveThreadPool.QueueUserWorkItem(() =>
						{
							try
							{
								if (!GreylistManager.IsWhitelisted(senderEndpoint.Address) && GreylistManager.IsBlacklisted(senderEndpoint.Address)) return;
								if (GreylistManager.IsGreylisted(senderEndpoint.Address)) return;
								ProcessMessage(receiveBytes, senderEndpoint);
							}
							catch (Exception e)
							{
								Log.Warn(string.Format("Process message error from: {0}", senderEndpoint.Address), e);
							}
						});
					}
					else
					{
						Log.Warn("Unexpected end of transmission?");
						continue;
					}
				}
				catch (Exception e)
				{
					Log.Error("Unexpected end of transmission?", e);
					if (listener.Client != null)
					{
						continue;
					}

					return;
				}
			}
		}

		private void ProcessMessage(byte[] receiveBytes, IPEndPoint senderEndpoint)
		{
			byte msgId = receiveBytes[0];

			if (msgId == 0xFE)
			{
				Log.InfoFormat("A query detected from: {0}", senderEndpoint.Address);
				HandleQuery(receiveBytes, senderEndpoint);
			}
			else if (msgId <= (byte) DefaultMessageIdTypes.ID_USER_PACKET_ENUM)
			{
				HandleRakNetMessage(receiveBytes, senderEndpoint, msgId);
			}
			else
			{
				PlayerNetworkSession playerSession;
				if (!_playerSessions.TryGetValue(senderEndpoint, out playerSession))
				{
					//Log.DebugFormat("Receive MCPE message 0x{1:x2} without session {0}", senderEndpoint.Address, msgId);
					//if (!_badPacketBans.ContainsKey(senderEndpoint.Address))
					//{
					//	_badPacketBans.Add(senderEndpoint.Address, true);
					//}
					return;
				}

				if (playerSession.MessageHandler == null)
				{
					Log.ErrorFormat("Receive MCPE message 0x{1:x2} without message handler {0}. Session removed.", senderEndpoint.Address, msgId);
					_playerSessions.TryRemove(senderEndpoint, out playerSession);
					//if (!_badPacketBans.ContainsKey(senderEndpoint.Address))
					//{
					//	_badPacketBans.Add(senderEndpoint.Address, true);
					//}
					return;
				}

				if (playerSession.Evicted) return;

				playerSession.LastUpdatedTime = DateTime.UtcNow;

				DatagramHeader header = new DatagramHeader(receiveBytes[0]);
				if (!header.isACK && !header.isNAK && header.isValid)
				{
					if (receiveBytes[0] == 0xa0)
					{
						throw new Exception("Receive ERROR, NAK in wrong place");
					}

					ConnectedPackage package = ConnectedPackage.CreateObject();
					try
					{
						package.Decode(receiveBytes);
					}
					catch (Exception e)
					{
						playerSession.Disconnect("Bad package received from client.");

						Log.Warn($"Bad packet {receiveBytes[0]}\n{Package.HexDump(receiveBytes)}", e);

						GreylistManager.Blacklist(senderEndpoint.Address);

						return;
					}


					// IF reliable code below is enabled, useItem start sending doubles
					// for some unknown reason.

					//Reliability reliability = package._reliability;
					//if (reliability == Reliability.Reliable
					//	|| reliability == Reliability.ReliableSequenced
					//	|| reliability == Reliability.ReliableOrdered
					//	)
					{
						EnqueueAck(playerSession, package._datagramSequenceNumber);
						//if (Log.IsDebugEnabled) Log.Debug("ACK on #" + package._datagramSequenceNumber.IntValue());
					}

					HandleConnectedPackage(playerSession, package);
					package.PutPool();
				}
				else if (header.isACK && header.isValid)
				{
					HandleAck(playerSession, receiveBytes);
				}
				else if (header.isNAK && header.isValid)
				{
					HandleNak(playerSession, receiveBytes);
				}
				else if (!header.isValid)
				{
					Log.Warn("!!!! ERROR, Invalid header !!!!!");
				}
			}
		}

		private ConcurrentDictionary<IPEndPoint, DateTime> _connectionAttemps = new ConcurrentDictionary<IPEndPoint, DateTime>();
		private DedicatedThreadPool _receiveThreadPool;

		private void HandleRakNetMessage(byte[] receiveBytes, IPEndPoint senderEndpoint, byte msgId)
		{
			DefaultMessageIdTypes msgIdType = (DefaultMessageIdTypes) msgId;

			// Increase fast, decrease slow on 1s ticks.
			if (ServerInfo.NumberOfPlayers < ServerInfo.PlayerSessions.Count) ServerInfo.NumberOfPlayers = ServerInfo.PlayerSessions.Count;

			// Shortcut to reply fast, and no parsing
			if (msgIdType == DefaultMessageIdTypes.ID_OPEN_CONNECTION_REQUEST_1)
			{
				if (!GreylistManager.AcceptConnection(senderEndpoint.Address))
				{
					var noFree = NoFreeIncomingConnections.CreateObject();
					var bytes = noFree.Encode();
					noFree.PutPool();

					TraceSend(noFree);

					SendData(bytes, senderEndpoint);
					Interlocked.Increment(ref ServerInfo.NumberOfDeniedConnectionRequestsPerSecond);
					return;
				}
			}

			Package message = null;
			try
			{
				try
				{
					message = PackageFactory.CreatePackage(msgId, receiveBytes, "raknet");
				}
				catch (Exception)
				{
					message = null;
				}

				if (message == null)
				{
					GreylistManager.Blacklist(senderEndpoint.Address);
					Log.ErrorFormat("Receive bad packet with ID: {0} (0x{0:x2}) {2} from {1}", msgId, senderEndpoint.Address, (DefaultMessageIdTypes) msgId);

					return;
				}

				TraceReceive(message);

				switch (msgIdType)
				{
					case DefaultMessageIdTypes.ID_UNCONNECTED_PING:
					case DefaultMessageIdTypes.ID_UNCONNECTED_PING_OPEN_CONNECTIONS:
					{
						HandleRakNetMessage(senderEndpoint, (UnconnectedPing) message);
						break;
					}
					case DefaultMessageIdTypes.ID_OPEN_CONNECTION_REQUEST_1:
					{
						HandleRakNetMessage(senderEndpoint, (OpenConnectionRequest1) message);
						break;
					}
					case DefaultMessageIdTypes.ID_OPEN_CONNECTION_REQUEST_2:
					{
						HandleRakNetMessage(senderEndpoint, (OpenConnectionRequest2) message);
						break;
					}
					default:
						GreylistManager.Blacklist(senderEndpoint.Address);
						Log.ErrorFormat("Receive unexpected packet with ID: {0} (0x{0:x2}) {2} from {1}", msgId, senderEndpoint.Address, (DefaultMessageIdTypes) msgId);
						break;
				}
			}
			finally
			{
				if (message != null) message.PutPool();
			}
		}

		private void HandleRakNetMessage(IPEndPoint senderEndpoint, UnconnectedPing incoming)
		{
			//TODO: This needs to be verified with RakNet first
			//response.sendpingtime = msg.sendpingtime;
			//response.sendpongtime = DateTimeOffset.UtcNow.Ticks / TimeSpan.TicksPerMillisecond;

			if (Config.GetProperty("EnableEdu", false))
			{
				var packet = UnconnectedPong.CreateObject();
				packet.serverId = senderEndpoint.Address.Address + senderEndpoint.Port;
				packet.pingId = incoming.pingId;
				packet.serverName = MotdProvider.GetMotd(ServerInfo, senderEndpoint, true);
				var data = packet.Encode();
				packet.PutPool();

				TraceSend(packet);

				SendData(data, senderEndpoint);
			}

			{
				var packet = UnconnectedPong.CreateObject();
				packet.serverId = senderEndpoint.Address.Address + senderEndpoint.Port;
				packet.pingId = incoming.pingId;
				packet.serverName = MotdProvider.GetMotd(ServerInfo, senderEndpoint);
				var data = packet.Encode();
				packet.PutPool();

				TraceSend(packet);

				SendData(data, senderEndpoint);
			}

			return;
		}

		private void HandleRakNetMessage(IPEndPoint senderEndpoint, OpenConnectionRequest1 incoming)
		{
			lock (_playerSessions)
			{
				// Already connecting, then this is just a duplicate
				if (_connectionAttemps.ContainsKey(senderEndpoint))
				{
					DateTime created;
					_connectionAttemps.TryGetValue(senderEndpoint, out created);

					if (DateTime.UtcNow < created + TimeSpan.FromSeconds(3))
					{
						return;
					}

					_connectionAttemps.TryRemove(senderEndpoint, out created);
				}

				if (!_connectionAttemps.TryAdd(senderEndpoint, DateTime.UtcNow)) return;
			}

			if (Log.IsDebugEnabled)
				Log.WarnFormat("New connection from: {0} {1}, MTU: {2}, Ver: {3}", senderEndpoint.Address, senderEndpoint.Port, incoming.mtuSize, incoming.raknetProtocolVersion);

			var packet = OpenConnectionReply1.CreateObject();
			packet.serverGuid = 12345;
			packet.mtuSize = incoming.mtuSize;
			packet.serverHasSecurity = 0;
			var data = packet.Encode();
			packet.PutPool();

			TraceSend(packet);

			SendData(data, senderEndpoint);
		}

		private void HandleRakNetMessage(IPEndPoint senderEndpoint, OpenConnectionRequest2 incoming)
		{
			PlayerNetworkSession session;
			lock (_playerSessions)
			{
				DateTime trash;
				if (!_connectionAttemps.TryRemove(senderEndpoint, out trash))
				{
					Log.WarnFormat("Unexpected connection request packet from {0}. Probably a resend.", senderEndpoint.Address);
					return;
				}

				if (_playerSessions.TryGetValue(senderEndpoint, out session))
				{
					// Already connecting, then this is just a duplicate
					if (session.State == ConnectionState.Connecting /* && DateTime.UtcNow < session.LastUpdatedTime + TimeSpan.FromSeconds(2)*/)
					{
						return;
					}

					Log.InfoFormat("Unexpected session from {0}. Removing old session and disconnecting old player.", senderEndpoint.Address);

					session.Disconnect("Reconnecting.", false);

					_playerSessions.TryRemove(senderEndpoint, out session);
				}

				session = new PlayerNetworkSession(this, null, senderEndpoint, incoming.mtuSize)
				{
					State = ConnectionState.Connecting,
					LastUpdatedTime = DateTime.UtcNow,
					MtuSize = incoming.mtuSize,
					NetworkIdentifier = incoming.clientGuid
				};

				_playerSessions.TryAdd(senderEndpoint, session);
			}

			//Player player = PlayerFactory.CreatePlayer(this, senderEndpoint);
			//player.ClientGuid = incoming.clientGuid;
			//player.NetworkHandler = session;
			//session.Player = player;
			session.MessageHandler = new LoginMessageHandler(session);

			var reply = OpenConnectionReply2.CreateObject();
			reply.serverGuid = 12345;
			reply.clientEndpoint = senderEndpoint;
			reply.mtuSize = incoming.mtuSize;
			reply.doSecurityAndHandshake = new byte[1];
			var data = reply.Encode();
			reply.PutPool();

			TraceSend(reply);

			SendData(data, senderEndpoint);
		}

		private void HandleConnectedPackage(PlayerNetworkSession playerSession, ConnectedPackage package)
		{
			foreach (var message in package.Messages)
			{
				if (message is SplitPartPackage)
				{
					HandleSplitMessage(playerSession, (SplitPartPackage) message);
					continue;
				}

				message.Timer.Restart();
				HandlePackage(message, playerSession);
			}
		}

		private void HandleSplitMessage(PlayerNetworkSession playerSession, SplitPartPackage splitMessage)
		{
			int spId = splitMessage.SplitId;
			int spIdx = splitMessage.SplitIdx;
			int spCount = splitMessage.SplitCount;

			Int24 sequenceNumber = splitMessage.DatagramSequenceNumber;
			Reliability reliability = splitMessage.Reliability;
			Int24 reliableMessageNumber = splitMessage.ReliableMessageNumber;
			Int24 orderingIndex = splitMessage.OrderingIndex;
			byte orderingChannel = splitMessage.OrderingChannel;

			SplitPartPackage[] spPackets;
			bool haveEmpty = false;

			// Need sync for this part since they come very fast, and very close in time. 
			// If no synk, will often detect complete message two times (or more).
			lock (playerSession.Splits)
			{
				if (!playerSession.Splits.ContainsKey(spId))
				{
					playerSession.Splits.TryAdd(spId, new SplitPartPackage[spCount]);
				}

				spPackets = playerSession.Splits[spId];
				if (spPackets[spIdx] != null)
				{
					Log.Debug("Already had splitpart (resent). Ignore this part.");
					return;
				}
				spPackets[spIdx] = splitMessage;

				for (int i = 0; i < spPackets.Length; i++)
				{
					haveEmpty = haveEmpty || spPackets[i] == null;
				}
			}

			if (!haveEmpty)
			{
				Log.DebugFormat("Got all {0} split packages for split ID: {1}", spCount, spId);

				SplitPartPackage[] waste;
				playerSession.Splits.TryRemove(spId, out waste);

				using (MemoryStream stream = MemoryStreamManager.GetStream())
				{
					for (int i = 0; i < spPackets.Length; i++)
					{
						SplitPartPackage splitPartPackage = spPackets[i];
						byte[] buf = splitPartPackage.Message;
						if (buf == null)
						{
							Log.Error("Expected bytes in splitpart, but got none");
							continue;
						}

						stream.Write(buf, 0, buf.Length);
						splitPartPackage.PutPool();
					}

					byte[] buffer = stream.ToArray();
					try
					{
						ConnectedPackage newPackage = ConnectedPackage.CreateObject();
						newPackage._datagramSequenceNumber = sequenceNumber;
						newPackage._reliability = reliability;
						newPackage._reliableMessageNumber = reliableMessageNumber;
						newPackage._orderingIndex = orderingIndex;
						newPackage._orderingChannel = (byte) orderingChannel;
						newPackage._hasSplit = false;

						Package fullMessage = PackageFactory.CreatePackage(buffer[0], buffer, "raknet") ??
						                      new UnknownPackage(buffer[0], buffer);
						fullMessage.DatagramSequenceNumber = sequenceNumber;
						fullMessage.Reliability = reliability;
						fullMessage.ReliableMessageNumber = reliableMessageNumber;
						fullMessage.OrderingIndex = orderingIndex;
						fullMessage.OrderingChannel = orderingChannel;

						newPackage.Messages = new List<Package>();
						newPackage.Messages.Add(fullMessage);

						Log.Debug(
							$"Assembled split package {newPackage._reliability} message #{newPackage._reliableMessageNumber}, Chan: #{newPackage._orderingChannel}, OrdIdx: #{newPackage._orderingIndex}");
						HandleConnectedPackage(playerSession, newPackage);
						newPackage.PutPool();
					}
					catch (Exception e)
					{
						Log.Error("Error during split message parsing", e);
						if (Log.IsDebugEnabled)
							Log.Debug($"0x{buffer[0]:x2}\n{Package.HexDump(buffer)}");
						playerSession.Disconnect("Bad package received from client.", false);
					}
				}
			}
		}

		private void HandleQuery(byte[] receiveBytes, IPEndPoint senderEndpoint)
		{
			if (!Config.GetProperty("EnableQuery", false)) return;

			if (receiveBytes[0] != 0xFE || receiveBytes[1] != 0xFD) return;

			byte packetId = receiveBytes[2];
			switch (packetId)
			{
				case 0x09:
				{
					byte[] buffer = new byte[17];
					// ID
					buffer[0] = 0x09;

					// Sequence number
					buffer[1] = receiveBytes[3];
					buffer[2] = receiveBytes[4];
					buffer[3] = receiveBytes[5];
					buffer[4] = receiveBytes[6];

					// Textual representation of int32 (token) with null terminator
					string str = new Random().Next().ToString(CultureInfo.InvariantCulture) + "\x00";
					Buffer.BlockCopy(str.ToCharArray(), 0, buffer, 5, 11);

					_listener.Send(buffer, buffer.Length, senderEndpoint);
					break;
				}
				case 0x00:
				{
					using (var stream = MemoryStreamManager.GetStream())
					{
						bool isFullStatRequest = receiveBytes.Length == 15;
						if (Log.IsInfoEnabled) Log.InfoFormat("Full request: {0}", isFullStatRequest);

						// ID
						stream.WriteByte(0x00);

						// Sequence number
						stream.WriteByte(receiveBytes[3]);
						stream.WriteByte(receiveBytes[4]);
						stream.WriteByte(receiveBytes[5]);
						stream.WriteByte(receiveBytes[6]);

						//{
						//	string str = "splitnum\0";
						//	byte[] bytes = Encoding.ASCII.GetBytes(str.ToCharArray());
						//	stream.Write(bytes, 0, bytes.Length);
						//}

						MotdProvider.GetMotd(ServerInfo, senderEndpoint); // Force update the player counts :-)

						var data = new Dictionary<string, string>
						{
							{"splitnum", "" + (char) 128},
							{"hostname", "Minecraft PE Server"},
							{"gametype", "SMP"},
							{"game_id", "MINECRAFTPE"},
							{"version", "0.15.0"},
							{"server_engine", "MiNET v1.0.0"},
							{"plugins", "MiNET v1.0.0"},
							{"map", "world"},
							{"numplayers", MotdProvider.NumberOfPlayers.ToString()},
							{"maxplayers", MotdProvider.MaxNumberOfPlayers.ToString()},
							{"whitelist", "off"},
							//{"hostip", "192.168.0.1"},
							//{"hostport", "19132"}
						};

						foreach (KeyValuePair<string, string> valuePair in data)
						{
							string key = valuePair.Key + "\x00" + valuePair.Value + "\x00";
							byte[] bytes = Encoding.ASCII.GetBytes(key.ToCharArray());
							stream.Write(bytes, 0, bytes.Length);
						}

						{
							string str = "\x00\x01player_\x00\x00";
							byte[] bytes = Encoding.ASCII.GetBytes(str.ToCharArray());
							stream.Write(bytes, 0, bytes.Length);
						}

						// End the stream with 0 byte
						stream.WriteByte(0);
						var buffer = stream.ToArray();
						_listener.Send(buffer, buffer.Length, senderEndpoint);
					}
					break;
				}
				default:
					return;
			}
		}

		private void HandleNak(PlayerNetworkSession session, byte[] receiveBytes)
		{
			if (session == null) return;

			Nak nak = Nak.CreateObject();
			nak.Reset();
			nak.Decode(receiveBytes);

			var queue = session.WaitingForAcksQueue;

			foreach (Tuple<int, int> range in nak.ranges)
			{
				Interlocked.Increment(ref ServerInfo.NumberOfNakReceive);

				int start = range.Item1;
				int end = range.Item2;

				for (int i = start; i <= end; i++)
				{
					session.ErrorCount++;

					// HACK: Just to make sure we aren't getting unessecary load on the queue during heavy buffering.
					//if (ServerInfo.AvailableBytes > 1000) continue;

					Datagram datagram;
					//if (queue.TryRemove(i, out datagram))
					if (!session.Evicted && queue.TryRemove(i, out datagram))
					{
						// RTT = RTT * 0.875 + rtt * 0.125
						// RTTVar = RTTVar * 0.875 + abs(RTT - rtt)) * 0.125
						// RTO = RTT + 4 * RTTVar
						long rtt = datagram.Timer.ElapsedMilliseconds;
						long RTT = session.Rtt;
						long RTTVar = session.RttVar;

						session.Rtt = (long) (RTT*0.875 + rtt*0.125);
						session.RttVar = (long) (RTTVar*0.875 + Math.Abs(RTT - rtt)*0.125);
						session.Rto = session.Rtt + 4*session.RttVar + 100; // SYNC time in the end

						FastThreadPool.QueueUserWorkItem(delegate
						{
							var dgram = (Datagram) datagram;
							if (Log.IsDebugEnabled)
								Log.WarnFormat("NAK, resent datagram #{0} for {1}", dgram.Header.datagramSequenceNumber, session.Username);
							SendDatagram(session, dgram);
							Interlocked.Increment(ref ServerInfo.NumberOfResends);
						});
					}
					else
					{
						if (Log.IsDebugEnabled)
							Log.WarnFormat("NAK, no datagram #{0} for {1}", i, session.Username);
					}
				}
			}

			nak.PutPool();
		}

		private void HandleAck(PlayerNetworkSession session, byte[] receiveBytes)
		{
			if (session == null) return;

			//Ack ack = Ack.CreateObject();
			Ack ack = new Ack();
			//ack.Reset();
			ack.Decode(receiveBytes);

			var queue = session.WaitingForAcksQueue;

			foreach (Tuple<int, int> range in ack.ranges)
			{
				Interlocked.Increment(ref ServerInfo.NumberOfAckReceive);

				int start = range.Item1;
				int end = range.Item2;
				for (int i = start; i <= end; i++)
				{
					Datagram datagram;
					if (queue.TryRemove(i, out datagram))
					{
						//if (Log.IsDebugEnabled)
						//	Log.DebugFormat("ACK, on datagram #{0} for {2}. Queue size={1}", i, queue.Count, player.Username);

						// RTT = RTT * 0.875 + rtt * 0.125
						// RTTVar = RTTVar * 0.875 + abs(RTT - rtt)) * 0.125
						// RTO = RTT + 4 * RTTVar
						long rtt = datagram.Timer.ElapsedMilliseconds;
						long RTT = session.Rtt;
						long RTTVar = session.RttVar;

						session.Rtt = (long) (RTT*0.875 + rtt*0.125);
						session.RttVar = (long) (RTTVar*0.875 + Math.Abs(RTT - rtt)*0.125);
						session.Rto = session.Rtt + 4*session.RttVar + 100; // SYNC time in the end

						datagram.PutPool();
					}
					else
					{
						if (Log.IsDebugEnabled)
							Log.WarnFormat("ACK, Failed to remove datagram #{0} for {2}. Queue size={1}", i, queue.Count, session.Username);
					}
				}
			}

			//ack.PutPool();

			session.ResendCount = 0;
			session.WaitForAck = false;
		}

		internal void HandlePackage(Package message, PlayerNetworkSession playerSession)
		{
			if (message == null)
			{
				return;
			}

			if (message.Reliability == Reliability.ReliableOrdered)
			{
				if (ForceOrderingForAll == false && (playerSession.CryptoContext == null || playerSession.CryptoContext.UseEncryption == false))
				{
					playerSession.AddToProcessing(message);
				}
				else
				{
					FastThreadPool.QueueUserWorkItem(() => playerSession.AddToProcessing(message));
				}

				return;
			}

			playerSession.HandlePackage(message, playerSession);
		}

		private void EnqueueAck(PlayerNetworkSession session, int sequenceNumber)
		{
			session.PlayerAckQueue.Enqueue(sequenceNumber);
		}

		public void SendPackage(PlayerNetworkSession session, Package message)
		{
			foreach (var datagram in Datagram.CreateDatagrams(message, session.MtuSize, session))
			{
				SendDatagram(session, datagram);
			}

			message.PutPool();
		}

		internal void SendDatagram(PlayerNetworkSession session, Datagram datagram)
		{
			if (datagram.MessageParts.Count == 0)
			{
				datagram.PutPool();
				Log.WarnFormat("Failed to resend #{0}", datagram.Header.datagramSequenceNumber.IntValue());
				return;
			}

			if (datagram.TransmissionCount > 10)
			{
				if (Log.IsDebugEnabled)
					Log.WarnFormat("TIMEOUT, Retransmission count remove from ACK queue #{0} Type: {2} (0x{2:x2}) for {1}",
						datagram.Header.datagramSequenceNumber.IntValue(),
						session.Username,
						datagram.FirstMessageId);

				datagram.PutPool();

				Interlocked.Increment(ref ServerInfo.NumberOfFails);
				return;
			}

			datagram.Header.datagramSequenceNumber = Interlocked.Increment(ref session.DatagramSequenceNumber);
			datagram.TransmissionCount++;

			byte[] data = datagram.Encode();

			datagram.Timer.Restart();

			if (!session.WaitingForAcksQueue.TryAdd(datagram.Header.datagramSequenceNumber.IntValue(), datagram))
			{
				Log.Warn(string.Format("Datagram sequence unexpectedly existed in the ACK/NAK queue already {0}", datagram.Header.datagramSequenceNumber.IntValue()));
			}

			lock (session.SyncRoot)
			{
				SendData(data, session.EndPoint);
			}
		}


		internal void SendData(byte[] data, IPEndPoint targetEndPoint)
		{
			try
			{
				_listener.Send(data, data.Length, targetEndPoint); // Less thread-issues it seems

				Interlocked.Increment(ref ServerInfo.NumberOfPacketsOutPerSecond);
				Interlocked.Add(ref ServerInfo.TotalPacketSizeOut, data.Length);
			}
			catch (ObjectDisposedException e)
			{
			}
			catch (Exception e)
			{
				//if (_listener == null || _listener.Client != null) Log.Error(string.Format("Send data lenght: {0}", data.Length), e);
			}
		}

		internal static void TraceReceive(Package message)
		{
			if (!Log.IsDebugEnabled) return;

			try
			{
				string typeName = message.GetType().Name;

				string includePattern = Config.GetProperty("TracePackets.Include", ".*");
				string excludePattern = Config.GetProperty("TracePackets.Exclude", null);
				int verbosity = Config.GetProperty("TracePackets.Verbosity", 0);
				verbosity = Config.GetProperty($"TracePackets.Verbosity.{typeName}", verbosity);

				if (!Regex.IsMatch(typeName, includePattern))
				{
					return;
				}

				if (!string.IsNullOrWhiteSpace(excludePattern) && Regex.IsMatch(typeName, excludePattern))
				{
					return;
				}

				if (verbosity == 0)
				{
					Log.Debug($"> Receive: {message.Id} (0x{message.Id:x2}): {message.GetType().Name}");
				}
				else if (verbosity == 1)
				{
					var jsonSerializerSettings = new JsonSerializerSettings
					{
						PreserveReferencesHandling = PreserveReferencesHandling.Arrays,

						Formatting = Formatting.Indented,
					};
					jsonSerializerSettings.Converters.Add(new NbtIntConverter());
					jsonSerializerSettings.Converters.Add(new NbtStringConverter());

					string result = JsonConvert.SerializeObject(message, jsonSerializerSettings);
					Log.Debug($"> Receive: {message.Id} (0x{message.Id:x2}): {message.GetType().Name}\n{result}");
				}
				else if (verbosity == 2)
				{
					Log.Debug($"> Receive: {message.Id} (0x{message.Id:x2}): {message.GetType().Name}\n{Package.HexDump(message.Bytes)}");
				}
			}
			catch (Exception e)
			{
				Log.Error("Error when printing trace", e);
			}
		}

		public static void TraceSend(Package message)
		{
			if (!Log.IsDebugEnabled) return;
			if (message is McpeWrapper) return;
			if (message is UnconnectedPong) return;
			if (message is McpeMovePlayer) return;
			//if (message is McpeSetEntityMotion) return;
			//if (message is McpeMoveEntity) return;
			if (message is McpeSetEntityData) return;
			if (message is McpeUpdateBlock) return;
			//if (!Debugger.IsAttached) return;

			Log.DebugFormat("<    Send: {0}: {1} (0x{0:x2})", message.Id, message.GetType().Name);
		}
	}

	public enum ServerRole
	{
		Node,
		Proxy,
		Full,
	}
}