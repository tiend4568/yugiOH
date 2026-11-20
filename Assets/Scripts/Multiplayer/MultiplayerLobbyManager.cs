using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using Unity.Collections;
using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
using Unity.Networking.Transport.Relay;
using Unity.Services.Authentication;
using Unity.Services.Core;
using Unity.Services.Lobbies;
using Unity.Services.Lobbies.Models;
using Unity.Services.Relay;
using Unity.Services.Relay.Models;
using UnityEngine;
using UnityEngine.SceneManagement;
using Debug = UnityEngine.Debug;

[Serializable]
public class LobbyStateDto
{
    public string roomId6 = "";
    public bool hasPassword = false;
    public bool isCloudMode = false;

    public string hostName = "Host";
    public bool hostReady = false;
    public int hostDeckCount = 0;
    public bool hostDeckValid = false;

    public bool hasClient = false;
    public string clientName = "";
    public bool clientReady = false;
    public int clientDeckCount = 0;
    public bool clientDeckValid = false;

    public int connectedCount = 0;
}

[Serializable]
public class LobbyHelloDto
{
    public string playerName = "Client";
    public string deckJson = "";
}

[Serializable]
public class LobbyReadyDto
{
    public bool isReady = false;
    public string deckJson = "";
}

[Serializable]
public class LobbyMatchStartingDto
{
    public string hostName = "Host";
    public string clientName = "Client";
    public int hostDeckCount = 0;
    public int clientDeckCount = 0;
}

/// <summary>
/// Quản lý Tạo phòng / Vào phòng 1v1 bằng ID Phòng 6 số + Mật khẩu phòng:
/// - Tự động kết nối qua Internet (Unity Lobby + Unity Relay) để chơi khác mạng Wi-Fi / 4G.
/// - Tự động Fallback mượt sang LAN / Localhost Discovery theo đúng ID 6 số + Mật khẩu nếu ngoại tuyến hoặc chưa bật Relay trên Dashboard.
/// </summary>
public class MultiplayerLobbyManager : MonoBehaviour
{
    public static MultiplayerLobbyManager Instance { get; private set; }

    public const int MaxPlayers = 2;
    public const string BattleSceneName = "BattleScene";
    public const string MainMenuSceneName = "SampleScene";
    public const int LanDiscoveryPort = 47777;

    private const string MSG_CLIENT_HELLO = "YGO_Lobby_ClientHello";
    private const string MSG_CLIENT_READY = "YGO_Lobby_ClientReady";
    private const string MSG_LOBBY_STATE = "YGO_Lobby_State";
    private const string MSG_MATCH_STARTING = "YGO_Lobby_MatchStarting";

    [Header("Cấu hình kết nối")]
    public float clientConnectTimeoutSeconds = 12f;

    public LobbyStateDto CurrentState { get; private set; } = new LobbyStateDto();
    public bool IsHostMode { get; private set; }
    public bool IsClientMode { get; private set; }
    public bool IsConnecting { get; private set; }
    public bool IsBusyCreatingOrJoining { get; private set; }
    public bool IsInRoom { get; private set; }
    public bool IsTransitioningToBattle { get; private set; }
    public bool IsUsingCloudRelay { get; private set; }

    public string CurrentRoomId { get; private set; } = "";
    public string CurrentRoomPassword { get; private set; } = "";
    public string LastCloudError { get; private set; } = "";

    public string LastStatusMessage { get; private set; } = "Nhập ID Phòng (6 số) & Mật khẩu để Tạo phòng hoặc Vào phòng.";
    public string HostLanIp { get; private set; } = "127.0.0.1";
    public ushort CurrentPort { get; private set; } = MatchSessionConfig.DefaultPort;
    public string TargetIp { get; private set; } = MatchSessionConfig.DefaultIp;
    public ulong ConnectedOpponentClientId => connectedClientId;

    public bool IsLocalReady => IsHostMode ? CurrentState.hostReady : CurrentState.clientReady;
    public bool CanHostStartMatch =>
        IsHostMode &&
        IsInRoom &&
        !IsTransitioningToBattle &&
        CurrentState.hasClient &&
        CurrentState.connectedCount >= MaxPlayers &&
        CurrentState.hostReady &&
        CurrentState.clientReady &&
        CurrentState.hostDeckValid &&
        CurrentState.clientDeckValid;

    public event Action<LobbyStateDto> OnLobbyStateChanged;
    public event Action<string> OnStatusChanged;
    public event Action<string> OnMatchDisconnect;

    private NetworkManager networkManager;
    private UnityTransport unityTransport;
    private bool callbacksRegistered = false;
    private bool messagingRegistered = false;
    private bool sceneCallbackRegistered = false;
    private ulong connectedClientId = ulong.MaxValue;
    private float connectTimer = 0f;
    private float activeConnectTimeoutSeconds = 6f;

    // Unity Cloud Lobby & LAN Discovery
    private Lobby activeCloudLobby;
    private Coroutine cloudHeartbeatCoroutine;
    private UdpClient lanDiscoveryServer;
    private Task<bool> cloudSignInTask;
    private int connectAttemptToken = 0;

    public static MultiplayerLobbyManager EnsureInstance()
    {
        if (Instance != null) return Instance;

        MultiplayerLobbyManager existing = FindObjectOfType<MultiplayerLobbyManager>();
        if (existing != null)
        {
            Instance = existing;
            return existing;
        }

        GameObject go = new GameObject("MultiplayerLobbyManager");
        DontDestroyOnLoad(go);
        Instance = go.AddComponent<MultiplayerLobbyManager>();
        return Instance;
    }

    void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }

        Instance = this;
        DontDestroyOnLoad(gameObject);
        Application.runInBackground = true;
        HostLanIp = GetLocalLanIPv4();
    }

    void Start()
    {
        // Tự động kết nối sẵn Unity Cloud Services ngay khi mở game để chế độ Internet luôn sẵn sàng
        _ = EnsureUnityServicesSignedInAsync();
    }

    void Update()
    {
        PollLanDiscoveryRequests();

        if (IsConnecting && IsClientMode && !IsInRoom && !IsBusyCreatingOrJoining)
        {
            connectTimer += Time.unscaledDeltaTime;
            if (connectTimer >= activeConnectTimeoutSeconds)
            {
                if (IsUsingCloudRelay)
                {
                    LeaveRoom($"Đã tìm thấy phòng Online ID {CurrentRoomId} nhưng kết nối Relay bị quá thời gian (Firewall/mạng chặn UDP). Hãy thử lại!");
                }
                else if (!string.IsNullOrEmpty(LastCloudError))
                {
                    LeaveRoom($"Chưa kết nối được Unity Cloud ({LastCloudError}) và không thấy phòng ID {CurrentRoomId} trong mạng LAN!");
                }
                else
                {
                    LeaveRoom($"Không tìm thấy phòng ID {CurrentRoomId}! Vui lòng kiểm tra lại mã phòng 6 số.");
                }
            }
        }
    }

    void OnApplicationQuit()
    {
        ShutdownNetworkImmediate();
    }

    void OnDestroy()
    {
        if (Instance == this)
        {
            ShutdownNetworkImmediate();
            Instance = null;
        }
    }

    private bool EnsureNetworkManagerReady()
    {
        if (networkManager == null)
        {
            networkManager = NetworkManager.Singleton != null
                ? NetworkManager.Singleton
                : FindObjectOfType<NetworkManager>();
        }

        if (networkManager == null)
        {
            GameObject nmObj = new GameObject("YugiohNetworkManager");
            DontDestroyOnLoad(nmObj);

            unityTransport = nmObj.AddComponent<UnityTransport>();
            networkManager = nmObj.AddComponent<NetworkManager>();

            if (networkManager.NetworkConfig == null)
            {
                networkManager.NetworkConfig = new NetworkConfig();
            }

            networkManager.NetworkConfig.NetworkTransport = unityTransport;
        }
        else
        {
            DontDestroyOnLoad(networkManager.gameObject);
            if (networkManager.NetworkConfig == null)
            {
                networkManager.NetworkConfig = new NetworkConfig();
            }

            unityTransport = networkManager.GetComponent<UnityTransport>();
            if (unityTransport == null)
            {
                unityTransport = networkManager.gameObject.AddComponent<UnityTransport>();
            }
            networkManager.NetworkConfig.NetworkTransport = unityTransport;
        }

        unityTransport.MaxPayloadSize = 32 * 1024;
        unityTransport.DisconnectTimeoutMS = 60000;
        unityTransport.HeartbeatTimeoutMS = 1000;

        networkManager.NetworkConfig.ConnectionApproval = true;
        networkManager.NetworkConfig.EnableSceneManagement = true;

        RegisterNetworkCallbacks();
        return true;
    }

    private void RegisterNetworkCallbacks()
    {
        if (networkManager == null || callbacksRegistered) return;

        networkManager.ConnectionApprovalCallback = HandleConnectionApproval;
        networkManager.OnClientConnectedCallback += HandleClientConnected;
        networkManager.OnClientDisconnectCallback += HandleClientDisconnected;
        networkManager.OnTransportFailure += HandleTransportFailure;
        callbacksRegistered = true;
    }

    private void UnregisterNetworkCallbacks()
    {
        if (networkManager == null || !callbacksRegistered) return;

        networkManager.ConnectionApprovalCallback = null;
        networkManager.OnClientConnectedCallback -= HandleClientConnected;
        networkManager.OnClientDisconnectCallback -= HandleClientDisconnected;
        networkManager.OnTransportFailure -= HandleTransportFailure;
        callbacksRegistered = false;
    }

    private void RegisterNamedMessageHandlers()
    {
        if (networkManager == null) return;

        if (networkManager.CustomMessagingManager != null && !messagingRegistered)
        {
            networkManager.CustomMessagingManager.RegisterNamedMessageHandler(MSG_CLIENT_HELLO, OnReceiveClientHello);
            networkManager.CustomMessagingManager.RegisterNamedMessageHandler(MSG_CLIENT_READY, OnReceiveClientReady);
            networkManager.CustomMessagingManager.RegisterNamedMessageHandler(MSG_LOBBY_STATE, OnReceiveLobbyState);
            networkManager.CustomMessagingManager.RegisterNamedMessageHandler(MSG_MATCH_STARTING, OnReceiveMatchStarting);
            messagingRegistered = true;
        }

        if (networkManager.SceneManager != null && !sceneCallbackRegistered)
        {
            networkManager.SceneManager.OnSceneEvent += HandleNetworkSceneEvent;
            sceneCallbackRegistered = true;
        }
    }

    private void UnregisterNamedMessageHandlers()
    {
        if (networkManager == null) return;

        if (networkManager.CustomMessagingManager != null && messagingRegistered)
        {
            networkManager.CustomMessagingManager.UnregisterNamedMessageHandler(MSG_CLIENT_HELLO);
            networkManager.CustomMessagingManager.UnregisterNamedMessageHandler(MSG_CLIENT_READY);
            networkManager.CustomMessagingManager.UnregisterNamedMessageHandler(MSG_LOBBY_STATE);
            networkManager.CustomMessagingManager.UnregisterNamedMessageHandler(MSG_MATCH_STARTING);
            messagingRegistered = false;
        }

        if (networkManager.SceneManager != null && sceneCallbackRegistered)
        {
            networkManager.SceneManager.OnSceneEvent -= HandleNetworkSceneEvent;
            sceneCallbackRegistered = false;
        }
    }

    // ==================== UNITY SERVICES (AUTH + RELAY + LOBBY) ====================

    private Task<bool> EnsureUnityServicesSignedInAsync()
    {
        if (UnityServices.State == ServicesInitializationState.Initialized &&
            AuthenticationService.Instance != null &&
            AuthenticationService.Instance.IsSignedIn)
        {
            return Task.FromResult(true);
        }

        if (cloudSignInTask != null && !cloudSignInTask.IsCompleted)
        {
            return cloudSignInTask;
        }

        cloudSignInTask = SignInUnityServicesInternalAsync();
        return cloudSignInTask;
    }

    private async Task<bool> EnsureUnityServicesSignedInWithTimeoutAsync(int timeoutMs)
    {
        Task<bool> signInTask = EnsureUnityServicesSignedInAsync();
        if (signInTask.IsCompleted)
        {
            return signInTask.Status == TaskStatus.RanToCompletion && signInTask.Result;
        }

        Task completed = await Task.WhenAny(signInTask, Task.Delay(timeoutMs));
        if (completed == signInTask && signInTask.Status == TaskStatus.RanToCompletion)
        {
            return signInTask.Result;
        }

        LastCloudError = $"Hết thời gian kết nối Unity Cloud ({timeoutMs / 1000}s)";
        Debug.LogWarning($"[MultiplayerLobby] Kết nối Unity Cloud Services quá {timeoutMs}ms (mạng chậm hoặc bị chặn). Tạm dùng chế độ LAN/Localhost.");
        return false;
    }

    private async Task<(bool ok, T result)> RunWithTimeoutAsync<T>(Task<T> task, int timeoutMs)
    {
        Task completed = await Task.WhenAny(task, Task.Delay(timeoutMs));
        if (completed == task)
        {
            return (true, await task);
        }
        _ = task.ContinueWith(_ => { }, TaskContinuationOptions.OnlyOnFaulted);
        return (false, default);
    }

    private async Task<bool> SignInUnityServicesInternalAsync()
    {
        try
        {
            if (UnityServices.State == ServicesInitializationState.Uninitialized)
            {
                var options = new InitializationOptions();
                // Tạo profile riêng cho từng tiến trình để mở 2 cửa sổ trên cùng 1 máy không bị trùng PlayerId
                string profile = Application.isEditor
                    ? "EditorDuelist"
                    : $"Player_{Process.GetCurrentProcess().Id}";
                options.SetProfile(profile);
                await UnityServices.InitializeAsync(options);
            }

            if (!AuthenticationService.Instance.IsSignedIn)
            {
                await AuthenticationService.Instance.SignInAnonymouslyAsync();
            }

            if (AuthenticationService.Instance.IsSignedIn)
            {
                LastCloudError = "";
                Debug.Log($"[MultiplayerLobby] Đã kết nối Unity Cloud Services sẵn sàng (PlayerId: {AuthenticationService.Instance.PlayerId}).");
            }
            return AuthenticationService.Instance.IsSignedIn;
        }
        catch (Exception ex)
        {
            LastCloudError = ex.Message;
            Debug.LogWarning($"[MultiplayerLobby] Không thể khởi tạo Unity Cloud Services ({ex.Message}). Sẽ dùng chế độ LAN/Localhost.");
            return false;
        }
    }

    private IEnumerator CloudLobbyHeartbeatRoutine(string lobbyId)
    {
        var wait = new WaitForSecondsRealtime(15f);
        while (IsHostMode && IsInRoom && activeCloudLobby != null && activeCloudLobby.Id == lobbyId)
        {
            yield return wait;
            if (IsHostMode && IsInRoom && activeCloudLobby != null && activeCloudLobby.Id == lobbyId)
            {
                _ = LobbyService.Instance.SendHeartbeatPingAsync(lobbyId);
            }
        }
    }

    private async void CleanupCloudLobbyAsync()
    {
        if (cloudHeartbeatCoroutine != null)
        {
            StopCoroutine(cloudHeartbeatCoroutine);
            cloudHeartbeatCoroutine = null;
        }

        if (activeCloudLobby != null)
        {
            string lobbyId = activeCloudLobby.Id;
            activeCloudLobby = null;
            try
            {
                await LobbyService.Instance.DeleteLobbyAsync(lobbyId);
            }
            catch
            {
                // Bỏ qua nếu lobby đã tự hết hạn
            }
        }
    }

    // ==================== LAN UDP DISCOVERY FALLBACK THEO ID 6 SỐ ====================

    private void StartLanDiscoveryResponder()
    {
        StopLanDiscoveryResponder();
        try
        {
            lanDiscoveryServer = new UdpClient();
            lanDiscoveryServer.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            lanDiscoveryServer.Client.Bind(new IPEndPoint(IPAddress.Any, LanDiscoveryPort));
            lanDiscoveryServer.EnableBroadcast = true;
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[MultiplayerLobby] Không mở cổng LAN Discovery {LanDiscoveryPort}: {ex.Message}");
            StopLanDiscoveryResponder();
        }
    }

    private void StopLanDiscoveryResponder()
    {
        if (lanDiscoveryServer != null)
        {
            try { lanDiscoveryServer.Close(); } catch { }
            lanDiscoveryServer = null;
        }
    }

    private void PollLanDiscoveryRequests()
    {
        if (!IsHostMode || !IsInRoom || lanDiscoveryServer == null) return;

        try
        {
            while (lanDiscoveryServer.Available > 0)
            {
                IPEndPoint remoteEP = new IPEndPoint(IPAddress.Any, 0);
                byte[] data = lanDiscoveryServer.Receive(ref remoteEP);
                if (data == null || data.Length == 0) continue;

                string msg = Encoding.UTF8.GetString(data);
                if (msg.StartsWith("YGO_FIND_ROOM:", StringComparison.Ordinal))
                {
                    string requestedId = msg.Substring("YGO_FIND_ROOM:".Length).Trim();
                    if (string.Equals(requestedId, CurrentRoomId, StringComparison.Ordinal))
                    {
                        string reply = $"YGO_ROOM_HERE:{CurrentRoomId}:{HostLanIp}:{CurrentPort}";
                        byte[] replyBytes = Encoding.UTF8.GetBytes(reply);
                        lanDiscoveryServer.Send(replyBytes, replyBytes.Length, remoteEP);
                    }
                }
            }
        }
        catch
        {
            // Bỏ qua lỗi đọc gói UDP lẻ
        }
    }

    private async Task<(bool found, string ip, ushort port)> DiscoverLanRoomByIdAsync(string roomId6, int timeoutMs = 450)
    {
        UdpClient client = null;
        try
        {
            client = new UdpClient(0);
            client.EnableBroadcast = true;

            byte[] reqBytes = Encoding.UTF8.GetBytes($"YGO_FIND_ROOM:{roomId6}");
            await client.SendAsync(reqBytes, reqBytes.Length, new IPEndPoint(IPAddress.Broadcast, LanDiscoveryPort));
            await client.SendAsync(reqBytes, reqBytes.Length, new IPEndPoint(IPAddress.Loopback, LanDiscoveryPort));

            float elapsed = 0f;
            while (elapsed < timeoutMs)
            {
                if (client.Available > 0)
                {
                    IPEndPoint ep = new IPEndPoint(IPAddress.Any, 0);
                    byte[] respBytes = client.Receive(ref ep);
                    string resp = Encoding.UTF8.GetString(respBytes);
                    // Định dạng: YGO_ROOM_HERE:<roomId6>:<hostLanIp>:<port>
                    string prefix = $"YGO_ROOM_HERE:{roomId6}:";
                    if (resp.StartsWith(prefix, StringComparison.Ordinal))
                    {
                        string rest = resp.Substring(prefix.Length);
                        string[] parts = rest.Split(':');
                        if (parts.Length >= 2 && ushort.TryParse(parts[1], out ushort p))
                        {
                            string foundIp = ep.Address != null && !IPAddress.IsLoopback(ep.Address)
                                ? ep.Address.ToString()
                                : parts[0];
                            return (true, foundIp, p);
                        }
                    }
                }
                await Task.Delay(40);
                elapsed += 40f;
            }
        }
        catch
        {
            // Fallback sang cổng tất định
        }
        finally
        {
            if (client != null)
            {
                try { client.Close(); } catch { }
            }
        }

        return (false, "127.0.0.1", MatchSessionConfig.GetDeterministicPortFromRoomId6(roomId6));
    }

    // ==================== TẠO PHÒNG & VÀO PHÒNG BẰNG ID 6 SỐ + MẬT KHẨU ====================

    public bool StartHostRoom(string playerName, string roomId6, string roomPassword)
    {
        if (IsBusyCreatingOrJoining) return false;
        EnsureNetworkManagerReady();

        if (networkManager.ShutdownInProgress)
        {
            SetStatus("Đang đóng phiên cũ, vui lòng thử lại sau 1 giây...");
            return false;
        }

        if (networkManager.IsListening)
        {
            LeaveRoom();
            SetStatus("Đã đóng phòng cũ, vui lòng bấm Tạo phòng lại.");
            return false;
        }

        string cleanRoomId = string.IsNullOrWhiteSpace(roomId6)
            ? MatchSessionConfig.GenerateRandomRoomId6()
            : roomId6.Trim();

        if (!MatchSessionConfig.IsValidRoomId6(cleanRoomId))
        {
            SetStatus("ID phòng phải gồm đúng 6 chữ số từ 0-9 (ví dụ: 123456)!");
            return false;
        }

        string cleanName = string.IsNullOrWhiteSpace(playerName) ? "Host" : playerName.Trim();
        string cleanPassword = string.IsNullOrEmpty(roomPassword) ? "" : roomPassword.Trim();

        MatchSessionConfig.SavePlayerName(cleanName);
        MatchSessionConfig.SaveRoomId6(cleanRoomId);
        MatchSessionConfig.SaveRoomPassword(cleanPassword);

        _ = StartHostRoomAsync(cleanName, cleanRoomId, cleanPassword);
        return true;
    }

    private bool HasRelayEndpoint(List<RelayServerEndpoint> endpoints, string connectionType)
    {
        if (endpoints == null) return false;
        for (int i = 0; i < endpoints.Count; i++)
        {
            var ep = endpoints[i];
            if (ep != null && string.Equals(ep.ConnectionType, connectionType, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    private async Task StartHostRoomAsync(string cleanName, string cleanRoomId, string cleanPassword)
    {
        int myToken = ++connectAttemptToken;
        IsBusyCreatingOrJoining = true;
        IsHostMode = true;
        IsClientMode = false;
        IsConnecting = true;
        IsInRoom = false;
        IsTransitioningToBattle = false;

        CurrentRoomId = cleanRoomId;
        CurrentRoomPassword = cleanPassword;
        HostLanIp = GetLocalLanIPv4();

        string hostDeckJson = MatchSessionConfig.BuildLocalDeckJson(out int hostDeckCount, out bool hostDeckValid, out string deckErr);
        MatchSessionConfig.HostDeckJson = hostDeckJson;
        MatchSessionConfig.HostDeckCount = hostDeckCount;
        MatchSessionConfig.ClientDeckJson = "";
        MatchSessionConfig.ClientDeckCount = 0;

        SetStatus($"Đang khởi tạo phòng ID {cleanRoomId}...");
        NotifyLobbyStateChanged();

        bool cloudSuccess = false;
        bool roomIdCollision = false;

        // 1. Thử tạo phòng qua Unity Cloud (Relay + Lobby) để chơi xuyên mạng Internet / khác Wi-Fi
        if (await EnsureUnityServicesSignedInWithTimeoutAsync(8000))
        {
            if (myToken != connectAttemptToken) return;

            try
            {
                // Kiểm tra trùng ID phòng 6 số đang mở trên Cloud
                var queryWrapped = await RunWithTimeoutAsync(
                    LobbyService.Instance.QueryLobbiesAsync(new QueryLobbiesOptions
                    {
                        Count = 5,
                        Filters = new List<QueryFilter>
                        {
                            new QueryFilter(QueryFilter.FieldOptions.S1, cleanRoomId, QueryFilter.OpOptions.EQ)
                        }
                    }),
                    5000
                );
                if (!queryWrapped.ok)
                {
                    throw new TimeoutException("Hết thời gian kiểm tra phòng trên Unity Cloud Lobby.");
                }
                var queryResult = queryWrapped.result;

                if (myToken != connectAttemptToken) return;

                if (queryResult != null && queryResult.Results != null)
                {
                    foreach (var existingLobby in queryResult.Results)
                    {
                        if (existingLobby == null) continue;
                        if (existingLobby.HostId == AuthenticationService.Instance.PlayerId)
                        {
                            try { await LobbyService.Instance.DeleteLobbyAsync(existingLobby.Id); } catch { }
                        }
                        else
                        {
                            roomIdCollision = true;
                            break;
                        }
                    }
                }

                if (myToken != connectAttemptToken) return;

                if (roomIdCollision)
                {
                    ResetLocalLobbyFlags();
                    SetStatus($"ID phòng {cleanRoomId} đang có người khác sử dụng! Hãy đổi 6 số khác hoặc bấm TẠO MÃ.");
                    NotifyLobbyStateChanged();
                    return;
                }

                Allocation alloc = await RelayService.Instance.CreateAllocationAsync(1);
                if (myToken != connectAttemptToken) return;

                string relayJoinCode = await RelayService.Instance.GetJoinCodeAsync(alloc.AllocationId);
                if (myToken != connectAttemptToken) return;

                var createOptions = new CreateLobbyOptions
                {
                    IsPrivate = false,
                    Data = new Dictionary<string, DataObject>
                    {
                        ["RoomId6"] = new DataObject(DataObject.VisibilityOptions.Public, cleanRoomId, DataObject.IndexOptions.S1),
                        ["RelayJoinCode"] = new DataObject(DataObject.VisibilityOptions.Public, relayJoinCode),
                        ["HasPassword"] = new DataObject(DataObject.VisibilityOptions.Public, string.IsNullOrEmpty(cleanPassword) ? "0" : "1")
                    }
                };

                Lobby createdLobby = await LobbyService.Instance.CreateLobbyAsync($"YGO_{cleanRoomId}", MaxPlayers, createOptions);
                if (myToken != connectAttemptToken)
                {
                    if (createdLobby != null)
                    {
                        try { await LobbyService.Instance.DeleteLobbyAsync(createdLobby.Id); } catch { }
                    }
                    return;
                }

                activeCloudLobby = createdLobby;
                string connType = HasRelayEndpoint(alloc.ServerEndpoints, "dtls") ? "dtls" : "udp";
                unityTransport.SetRelayServerData(new RelayServerData(alloc, connType));

                cloudSuccess = true;
                IsUsingCloudRelay = true;
            }
            catch (Exception ex)
            {
                if (myToken != connectAttemptToken) return;
                LastCloudError = ex.Message;
                Debug.LogWarning($"[MultiplayerLobby] Cloud Relay/Lobby chưa khả dụng ({ex.Message}) -> Tự động chuyển sang LAN/Localhost theo ID {cleanRoomId}.");
                CleanupCloudLobbyAsync();
                cloudSuccess = false;
            }
        }

        if (myToken != connectAttemptToken) return;

        // 2. Nếu Cloud chưa bật trên Dashboard hoặc không có Internet -> Tự động dùng LAN/Localhost theo đúng ID 6 số
        if (!cloudSuccess)
        {
            IsUsingCloudRelay = false;
            ushort deterministicPort = MatchSessionConfig.GetDeterministicPortFromRoomId6(cleanRoomId);
            CurrentPort = deterministicPort;
            TargetIp = "127.0.0.1";
            unityTransport.SetConnectionData("127.0.0.1", deterministicPort, "0.0.0.0");
            StartLanDiscoveryResponder();
        }

        CurrentState = new LobbyStateDto
        {
            roomId6 = cleanRoomId,
            hasPassword = !string.IsNullOrEmpty(cleanPassword),
            isCloudMode = IsUsingCloudRelay,
            hostName = cleanName,
            hostReady = false,
            hostDeckCount = hostDeckCount,
            hostDeckValid = hostDeckValid,
            hasClient = false,
            clientName = "",
            clientReady = false,
            clientDeckCount = 0,
            clientDeckValid = false,
            connectedCount = 1
        };
        connectedClientId = ulong.MaxValue;

        networkManager.NetworkConfig.ConnectionData = Encoding.UTF8.GetBytes(cleanPassword);

        bool started = networkManager.StartHost();
        IsBusyCreatingOrJoining = false;
        IsConnecting = false;

        if (!started)
        {
            CleanupCloudLobbyAsync();
            StopLanDiscoveryResponder();
            ResetLocalLobbyFlags();
            SetStatus($"Không thể tạo phòng ID {cleanRoomId}. Hãy thử đổi sang một mã 6 số khác!");
            NotifyLobbyStateChanged();
            return;
        }

        RegisterNamedMessageHandlers();
        if (IsUsingCloudRelay && activeCloudLobby != null)
        {
            if (cloudHeartbeatCoroutine != null) StopCoroutine(cloudHeartbeatCoroutine);
            cloudHeartbeatCoroutine = StartCoroutine(CloudLobbyHeartbeatRoutine(activeCloudLobby.Id));
        }

        IsInRoom = true;
        MatchSessionConfig.HostPlayerName = cleanName;
        MatchSessionConfig.LocalIsHost = true;

        string pwdText = string.IsNullOrEmpty(cleanPassword) ? "Không khóa" : cleanPassword;
        string modeText = IsUsingCloudRelay
            ? "Internet (Mọi mạng)"
            : $"CHỈ MẠNG LAN (Lỗi Cloud: {(string.IsNullOrEmpty(LastCloudError) ? "Chưa kết nối được Unity Cloud" : LastCloudError)})";
        string deckHint = hostDeckValid ? $"Bộ bài: {hostDeckCount} lá." : $"Cảnh báo: {deckErr}";

        SetStatus($"Đã tạo phòng [{modeText}] - ID: {cleanRoomId} | Mật khẩu: {pwdText}. {deckHint}");
        NotifyLobbyStateChanged();
    }

    public bool JoinRoom(string playerName, string roomId6, string roomPassword)
    {
        if (IsBusyCreatingOrJoining) return false;
        EnsureNetworkManagerReady();

        if (networkManager.ShutdownInProgress)
        {
            SetStatus("Đang đóng phiên cũ, vui lòng thử lại sau 1 giây...");
            return false;
        }

        if (networkManager.IsListening)
        {
            LeaveRoom();
            SetStatus("Đã ngắt kết nối cũ, vui lòng bấm Vào phòng lại.");
            return false;
        }

        string cleanRoomId = string.IsNullOrWhiteSpace(roomId6) ? "" : roomId6.Trim();
        if (!MatchSessionConfig.IsValidRoomId6(cleanRoomId))
        {
            SetStatus("Vui lòng nhập đúng ID phòng gồm 6 chữ số (0-9)!");
            return false;
        }

        string cleanName = string.IsNullOrWhiteSpace(playerName) ? "Client" : playerName.Trim();
        string cleanPassword = string.IsNullOrEmpty(roomPassword) ? "" : roomPassword.Trim();

        MatchSessionConfig.SavePlayerName(cleanName);
        MatchSessionConfig.SaveRoomId6(cleanRoomId);
        MatchSessionConfig.SaveRoomPassword(cleanPassword);

        _ = JoinRoomAsync(cleanName, cleanRoomId, cleanPassword);
        return true;
    }

    private async Task JoinRoomAsync(string cleanName, string cleanRoomId, string cleanPassword)
    {
        int myToken = ++connectAttemptToken;
        IsBusyCreatingOrJoining = true;
        IsHostMode = false;
        IsClientMode = true;
        IsConnecting = true;
        IsInRoom = false;
        IsTransitioningToBattle = false;
        connectTimer = 0f;

        CurrentRoomId = cleanRoomId;
        CurrentRoomPassword = cleanPassword;

        CurrentState = new LobbyStateDto
        {
            roomId6 = cleanRoomId,
            hasPassword = !string.IsNullOrEmpty(cleanPassword),
            hostName = "Host",
            hostReady = false,
            hasClient = false,
            clientName = cleanName,
            clientReady = false,
            connectedCount = 0
        };

        SetStatus($"Đang tìm phòng ID {cleanRoomId}...");
        NotifyLobbyStateChanged();

        bool configuredCloudRelay = false;
        bool cloudQueryCompleted = false;
        bool lanRoomFound = false;

        // 1. Tìm phòng trên Unity Cloud Lobby theo ID 6 số (chơi khác mạng Internet / 4G)
        if (await EnsureUnityServicesSignedInWithTimeoutAsync(8000))
        {
            if (myToken != connectAttemptToken) return;

            try
            {
                var queryOptions = new QueryLobbiesOptions
                {
                    Count = 5,
                    Filters = new List<QueryFilter>
                    {
                        new QueryFilter(QueryFilter.FieldOptions.S1, cleanRoomId, QueryFilter.OpOptions.EQ)
                    }
                };

                var queryWrapped = await RunWithTimeoutAsync(LobbyService.Instance.QueryLobbiesAsync(queryOptions), 5000);
                if (myToken != connectAttemptToken) return;

                if (queryWrapped.ok)
                {
                    cloudQueryCompleted = true;
                    QueryResponse res = queryWrapped.result;
                    if (res != null && res.Results != null && res.Results.Count > 0)
                    {
                        Lobby matchedLobby = res.Results[0];
                        if (matchedLobby.Data != null &&
                            matchedLobby.Data.TryGetValue("RelayJoinCode", out DataObject relayObj) &&
                            !string.IsNullOrWhiteSpace(relayObj.Value))
                        {
                            SetStatus($"Đã tìm thấy phòng Online ID {cleanRoomId}! Đang kết nối Relay...");
                            JoinAllocation joinAlloc = await RelayService.Instance.JoinAllocationAsync(relayObj.Value);
                            if (myToken != connectAttemptToken) return;

                            string connType = HasRelayEndpoint(joinAlloc.ServerEndpoints, "dtls") ? "dtls" : "udp";
                            unityTransport.SetRelayServerData(new RelayServerData(joinAlloc, connType));
                            configuredCloudRelay = true;
                            IsUsingCloudRelay = true;
                        }
                    }
                }
                else
                {
                    LastCloudError = "Hết thời gian tra cứu phòng trên Unity Cloud (5s)";
                }
            }
            catch (Exception ex)
            {
                if (myToken != connectAttemptToken) return;
                LastCloudError = ex.Message;
                Debug.LogWarning($"[MultiplayerLobby] Tìm phòng trên Cloud không thành công ({ex.Message}) -> Thử tìm trong mạng LAN/Localhost.");
                configuredCloudRelay = false;
            }
        }

        if (myToken != connectAttemptToken) return;

        // 2. Nếu không thấy trên Cloud -> Tìm tự động trong mạng LAN / Localhost theo đúng ID 6 số
        if (!configuredCloudRelay)
        {
            IsUsingCloudRelay = false;
            SetStatus($"Đang tìm phòng ID {cleanRoomId} trong mạng LAN / Localhost...");
            var lanResult = await DiscoverLanRoomByIdAsync(cleanRoomId, timeoutMs: 450);
            if (myToken != connectAttemptToken) return;

            lanRoomFound = lanResult.found;

            // Nếu Cloud đã tra cứu xong và LAN cũng không có phòng -> dừng ngay lập tức, không mở kết nối thừa
            if (!lanRoomFound && cloudQueryCompleted)
            {
                ResetLocalLobbyFlags();
                SetStatus($"Không tìm thấy phòng ID {cleanRoomId}! Vui lòng kiểm tra lại mã phòng 6 số.");
                NotifyLobbyStateChanged();
                return;
            }

            TargetIp = lanResult.ip;
            CurrentPort = lanResult.port;
            unityTransport.SetConnectionData(TargetIp, CurrentPort);
        }

        // Gửi mật khẩu phòng lên Host để xác thực trong ConnectionApprovalCallback
        networkManager.NetworkConfig.ConnectionData = Encoding.UTF8.GetBytes(cleanPassword);

        IsBusyCreatingOrJoining = false;
        connectTimer = 0f;
        // Nếu đã tìm thấy phòng (Cloud hoặc LAN) thì chờ tối đa 6s; nếu chỉ thử cục bộ dự phòng thì 1.5s là báo không tồn tại ngay
        activeConnectTimeoutSeconds = (configuredCloudRelay || lanRoomFound) ? clientConnectTimeoutSeconds : 1.5f;

        bool started = networkManager.StartClient();
        if (!started)
        {
            ResetLocalLobbyFlags();
            SetStatus($"Không thể khởi tạo kết nối tới phòng ID {cleanRoomId}.");
            NotifyLobbyStateChanged();
            return;
        }

        RegisterNamedMessageHandlers();
        SetStatus((configuredCloudRelay || lanRoomFound)
            ? $"Đang xác thực mật khẩu và vào phòng ID {cleanRoomId}..."
            : $"Đang kiểm tra nhanh phòng cục bộ ID {cleanRoomId}...");
        NotifyLobbyStateChanged();
    }

    public void ToggleLocalReady()
    {
        if (!IsInRoom || IsTransitioningToBattle) return;

        string localDeckJson = MatchSessionConfig.BuildLocalDeckJson(out int deckCount, out bool deckValid, out string deckErr);

        if (IsHostMode)
        {
            MatchSessionConfig.HostDeckJson = localDeckJson;
            MatchSessionConfig.HostDeckCount = deckCount;
            CurrentState.hostDeckCount = deckCount;
            CurrentState.hostDeckValid = deckValid;

            bool wantReady = !CurrentState.hostReady;
            if (wantReady && !deckValid)
            {
                CurrentState.hostReady = false;
                SetStatus($"Không thể Sẵn sàng: {deckErr}");
                BroadcastLobbyStateToClient();
                NotifyLobbyStateChanged();
                return;
            }

            CurrentState.hostReady = wantReady;
            SetStatus(CurrentState.hostReady
                ? $"Bạn (Host) đã Sẵn sàng (Bộ bài: {deckCount} lá)."
                : "Bạn (Host) đã hủy Sẵn sàng.");
            BroadcastLobbyStateToClient();
            NotifyLobbyStateChanged();
        }
        else if (IsClientMode && networkManager != null && networkManager.IsConnectedClient)
        {
            bool wantReady = !CurrentState.clientReady;
            if (wantReady && !deckValid)
            {
                SetStatus($"Không thể Sẵn sàng: {deckErr}");
                NotifyLobbyStateChanged();
                return;
            }

            CurrentState.clientReady = wantReady;
            CurrentState.clientDeckCount = deckCount;
            CurrentState.clientDeckValid = deckValid;

            SendJsonMessage(MSG_CLIENT_READY, NetworkManager.ServerClientId, new LobbyReadyDto
            {
                isReady = wantReady,
                deckJson = localDeckJson
            });

            SetStatus(wantReady
                ? $"Đã gửi bộ bài ({deckCount} lá) & trạng thái Sẵn sàng tới Host."
                : "Đã hủy trạng thái Sẵn sàng.");
            NotifyLobbyStateChanged();
        }
    }

    public void RequestStartMatch()
    {
        if (!IsHostMode || !IsInRoom || IsTransitioningToBattle) return;

        if (!CurrentState.hasClient || CurrentState.connectedCount < MaxPlayers)
        {
            SetStatus("Cần đủ 2 người chơi trong phòng mới có thể bắt đầu!");
            return;
        }

        if (!CurrentState.hostReady || !CurrentState.clientReady)
        {
            SetStatus("Cả 2 người chơi cần bấm SẴN SÀNG trước khi bắt đầu!");
            return;
        }

        if (!MatchSessionConfig.ValidateDeckJson(MatchSessionConfig.HostDeckJson, out int hostCnt, out string hostErr))
        {
            SetStatus($"Bộ bài của Host không hợp lệ: {hostErr}");
            return;
        }

        if (!MatchSessionConfig.ValidateDeckJson(MatchSessionConfig.ClientDeckJson, out int clientCnt, out string clientErr))
        {
            SetStatus($"Bộ bài của đối thủ không hợp lệ: {clientErr}");
            return;
        }

        IsTransitioningToBattle = true;
        MatchSessionConfig.CurrentMode = MatchMode.Multiplayer1v1;
        MatchSessionConfig.LocalIsHost = true;
        MatchSessionConfig.HostPlayerName = CurrentState.hostName;
        MatchSessionConfig.ClientPlayerName = CurrentState.clientName;
        MatchSessionConfig.HostDeckCount = hostCnt;
        MatchSessionConfig.ClientDeckCount = clientCnt;

        Debug.Log($"[MultiplayerLobby] Bắt đầu 1v1 (Phòng {CurrentRoomId}): Host={CurrentState.hostName} ({hostCnt} lá) vs Client={CurrentState.clientName} ({clientCnt} lá)");
        SetStatus("Đang đồng bộ bộ bài và chuyển cả 2 máy vào BattleScene...");

        if (connectedClientId != ulong.MaxValue)
        {
            SendJsonMessage(MSG_MATCH_STARTING, connectedClientId, new LobbyMatchStartingDto
            {
                hostName = CurrentState.hostName,
                clientName = CurrentState.clientName,
                hostDeckCount = hostCnt,
                clientDeckCount = clientCnt
            });
        }

        NotifyLobbyStateChanged();
        StartCoroutine(LoadBattleSceneSynchronizedRoutine());
    }

    private IEnumerator LoadBattleSceneSynchronizedRoutine()
    {
        yield return new WaitForSecondsRealtime(0.12f);

        if (networkManager == null || !networkManager.IsServer || networkManager.SceneManager == null)
        {
            IsTransitioningToBattle = false;
            SetStatus("Lỗi: NetworkSceneManager chưa sẵn sàng!");
            NotifyLobbyStateChanged();
            yield break;
        }

        SceneEventProgressStatus status = networkManager.SceneManager.LoadScene(BattleSceneName, LoadSceneMode.Single);
        if (status != SceneEventProgressStatus.Started)
        {
            IsTransitioningToBattle = false;
            MatchSessionConfig.ResetToSingleplayer();
            SetStatus($"Không thể chuyển sang {BattleSceneName} (Trạng thái: {status}).");
            NotifyLobbyStateChanged();
        }
    }

    public void LeaveMatchAndReturnToMenu(string customStatus = null)
    {
        LeaveRoom(customStatus);
        Time.timeScale = 1f;
        if (!SceneManager.GetActiveScene().name.Equals(MainMenuSceneName, StringComparison.OrdinalIgnoreCase))
        {
            SceneManager.LoadScene(MainMenuSceneName);
        }
    }

    public void LeaveRoom(string customStatus = null)
    {
        connectAttemptToken++;
        CleanupCloudLobbyAsync();
        StopLanDiscoveryResponder();

        ResetLocalLobbyFlags();
        MatchSessionConfig.ResetToSingleplayer();

        UnregisterNamedMessageHandlers();

        if (networkManager != null && (networkManager.IsListening || networkManager.IsClient || networkManager.IsServer))
        {
            networkManager.Shutdown();
        }

        SetStatus(string.IsNullOrEmpty(customStatus)
            ? "Đã rời phòng. Nhập ID Phòng (6 số) & Mật khẩu để Tạo phòng hoặc Vào phòng."
            : customStatus);
        NotifyLobbyStateChanged();
    }

    private void ShutdownNetworkImmediate()
    {
        connectAttemptToken++;
        CleanupCloudLobbyAsync();
        StopLanDiscoveryResponder();
        ResetLocalLobbyFlags();
        UnregisterNamedMessageHandlers();
        UnregisterNetworkCallbacks();
        if (networkManager != null && (networkManager.IsListening || networkManager.IsClient || networkManager.IsServer))
        {
            networkManager.Shutdown();
        }
    }

    private void ResetLocalLobbyFlags()
    {
        IsHostMode = false;
        IsClientMode = false;
        IsConnecting = false;
        IsBusyCreatingOrJoining = false;
        IsInRoom = false;
        IsTransitioningToBattle = false;
        IsUsingCloudRelay = false;
        connectTimer = 0f;
        connectedClientId = ulong.MaxValue;
        CurrentState = new LobbyStateDto();
    }

    // ==================== NGO CALLBACKS & XÁC THỰC MẬT KHẨU PHÒNG ====================

    private void HandleNetworkSceneEvent(SceneEvent sceneEvent)
    {
        if (sceneEvent == null) return;

        if (sceneEvent.SceneEventType == SceneEventType.Load &&
            !string.IsNullOrEmpty(sceneEvent.SceneName) &&
            sceneEvent.SceneName.Equals(BattleSceneName, StringComparison.OrdinalIgnoreCase))
        {
            MatchSessionConfig.CurrentMode = MatchMode.Multiplayer1v1;
            MatchSessionConfig.LocalIsHost = IsHostMode;
            IsTransitioningToBattle = true;
        }
        else if (sceneEvent.SceneEventType == SceneEventType.LoadEventCompleted &&
                 !string.IsNullOrEmpty(sceneEvent.SceneName) &&
                 sceneEvent.SceneName.Equals(BattleSceneName, StringComparison.OrdinalIgnoreCase))
        {
            IsTransitioningToBattle = false;
        }
    }

    private void HandleConnectionApproval(NetworkManager.ConnectionApprovalRequest request, NetworkManager.ConnectionApprovalResponse response)
    {
        bool isHostSelf = request.ClientNetworkId == NetworkManager.ServerClientId;
        int currentCount = networkManager != null ? networkManager.ConnectedClientsIds.Count : 0;

        if (isHostSelf)
        {
            response.Approved = true;
            response.CreatePlayerObject = false;
            response.Pending = false;
            return;
        }

        // 1. Kiểm tra mật khẩu phòng
        string clientPassword = (request.Payload != null && request.Payload.Length > 0)
            ? Encoding.UTF8.GetString(request.Payload)
            : "";

        if (!string.Equals(clientPassword, CurrentRoomPassword ?? "", StringComparison.Ordinal))
        {
            response.Approved = false;
            response.Reason = "Sai mật khẩu phòng!";
            response.Pending = false;
            return;
        }

        // 2. Kiểm tra trạng thái phòng & giới hạn 2 người
        if (IsTransitioningToBattle || MatchSessionConfig.IsMultiplayer)
        {
            response.Approved = false;
            response.Reason = "Phòng đang trong trận đấu!";
        }
        else if (currentCount < MaxPlayers)
        {
            response.Approved = true;
            response.CreatePlayerObject = false;
        }
        else
        {
            response.Approved = false;
            response.Reason = "Phòng đã đủ 2 người chơi!";
        }

        response.Pending = false;
    }

    private void HandleClientConnected(ulong clientId)
    {
        if (networkManager == null) return;

        if (IsHostMode)
        {
            if (clientId == NetworkManager.ServerClientId)
            {
                return;
            }

            connectedClientId = clientId;
            CurrentState.hasClient = true;
            CurrentState.clientName = $"Player #{clientId}";
            CurrentState.clientReady = false;
            CurrentState.clientDeckCount = 0;
            CurrentState.clientDeckValid = false;
            CurrentState.connectedCount = Mathf.Min(MaxPlayers, networkManager.ConnectedClientsIds.Count);

            SetStatus("Đối thủ đã nhập đúng mật khẩu và vào phòng (2/2)! Đang nhận bộ bài...");
            BroadcastLobbyStateToClient();
            NotifyLobbyStateChanged();
        }
        else if (IsClientMode && clientId == networkManager.LocalClientId)
        {
            IsConnecting = false;
            IsBusyCreatingOrJoining = false;
            IsInRoom = true;
            connectTimer = 0f;

            string localName = MatchSessionConfig.GetSavedPlayerName();
            string localDeckJson = MatchSessionConfig.BuildLocalDeckJson(out int deckCount, out bool deckValid, out _);

            CurrentState.clientDeckCount = deckCount;
            CurrentState.clientDeckValid = deckValid;

            SendJsonMessage(MSG_CLIENT_HELLO, NetworkManager.ServerClientId, new LobbyHelloDto
            {
                playerName = localName,
                deckJson = localDeckJson
            });

            SetStatus($"Đã vào phòng ID {CurrentRoomId} thành công! Đã gửi bộ bài ({deckCount} lá).");
            NotifyLobbyStateChanged();
        }
    }

    private void HandleClientDisconnected(ulong clientId)
    {
        if (networkManager == null) return;

        bool inBattleScene = SceneManager.GetActiveScene().name.Equals(BattleSceneName, StringComparison.OrdinalIgnoreCase);

        if (IsHostMode)
        {
            if (clientId == connectedClientId)
            {
                connectedClientId = ulong.MaxValue;
                CurrentState.hasClient = false;
                CurrentState.clientName = "";
                CurrentState.clientReady = false;
                CurrentState.clientDeckCount = 0;
                CurrentState.clientDeckValid = false;
                CurrentState.connectedCount = 1;
                MatchSessionConfig.ClientDeckJson = "";
                MatchSessionConfig.ClientDeckCount = 0;

                if (inBattleScene)
                {
                    string msg = "Đối thủ đã ngắt kết nối khỏi trận đấu!";
                    SetStatus(msg);
                    OnMatchDisconnect?.Invoke(msg);
                }
                else
                {
                    string pwdText = string.IsNullOrEmpty(CurrentRoomPassword) ? "Không khóa" : CurrentRoomPassword;
                    SetStatus($"Đối thủ đã rời phòng. Đang chờ đối thủ mới (1/2) - ID Phòng: {CurrentRoomId} | Mật khẩu: {pwdText}");
                    NotifyLobbyStateChanged();
                }
            }
        }
        else if (IsClientMode)
        {
            string reason = networkManager.DisconnectReason;
            string msg = !string.IsNullOrEmpty(reason)
                ? $"Không thể vào phòng: {reason}"
                : (IsInRoom ? "Đã mất kết nối với Host (phòng đã đóng)." : $"Không tìm thấy phòng ID {CurrentRoomId} hoặc sai mật khẩu.");

            if (inBattleScene)
            {
                LeaveRoom(msg);
                OnMatchDisconnect?.Invoke(msg);
            }
            else
            {
                LeaveRoom(msg);
            }
        }
    }

    private void HandleTransportFailure()
    {
        string msg = "Lỗi kết nối mạng (Transport Failure). Vui lòng thử lại.";
        bool inBattleScene = SceneManager.GetActiveScene().name.Equals(BattleSceneName, StringComparison.OrdinalIgnoreCase);
        LeaveRoom(msg);
        if (inBattleScene)
        {
            OnMatchDisconnect?.Invoke(msg);
        }
    }

    // ==================== CUSTOM NAMED MESSAGES ====================

    private void OnReceiveClientHello(ulong senderClientId, FastBufferReader reader)
    {
        if (!IsHostMode) return;

        LobbyHelloDto hello = ReadJsonMessage<LobbyHelloDto>(reader);
        if (hello == null) return;

        string cleanClientName = string.IsNullOrWhiteSpace(hello.playerName)
            ? $"Player #{senderClientId}"
            : hello.playerName.Trim();

        bool deckValid = MatchSessionConfig.ValidateDeckJson(hello.deckJson, out int deckCount, out string deckErr);

        connectedClientId = senderClientId;
        CurrentState.hasClient = true;
        CurrentState.clientName = cleanClientName;
        CurrentState.clientDeckCount = deckCount;
        CurrentState.clientDeckValid = deckValid;
        CurrentState.clientReady = false;
        CurrentState.connectedCount = 2;

        MatchSessionConfig.ClientPlayerName = cleanClientName;
        MatchSessionConfig.ClientDeckJson = deckValid ? hello.deckJson : "";
        MatchSessionConfig.ClientDeckCount = deckCount;

        string deckInfo = deckValid
            ? $"bộ bài hợp lệ ({deckCount} lá)"
            : $"bộ bài chưa hợp lệ ({deckErr})";

        SetStatus($"{cleanClientName} đã vào phòng (2/2) - {deckInfo}.");
        BroadcastLobbyStateToClient();
        NotifyLobbyStateChanged();
    }

    private void OnReceiveClientReady(ulong senderClientId, FastBufferReader reader)
    {
        if (!IsHostMode || senderClientId != connectedClientId) return;

        LobbyReadyDto payload = ReadJsonMessage<LobbyReadyDto>(reader);
        if (payload == null) return;

        if (!string.IsNullOrEmpty(payload.deckJson))
        {
            bool deckValid = MatchSessionConfig.ValidateDeckJson(payload.deckJson, out int deckCount, out string deckErr);
            CurrentState.clientDeckCount = deckCount;
            CurrentState.clientDeckValid = deckValid;
            MatchSessionConfig.ClientDeckJson = deckValid ? payload.deckJson : "";
            MatchSessionConfig.ClientDeckCount = deckCount;

            if (payload.isReady && !deckValid)
            {
                CurrentState.clientReady = false;
                SetStatus($"Từ chối Sẵn sàng của {CurrentState.clientName}: {deckErr}");
                BroadcastLobbyStateToClient();
                NotifyLobbyStateChanged();
                return;
            }
        }

        CurrentState.clientReady = payload.isReady && CurrentState.clientDeckValid;
        SetStatus(CurrentState.clientReady
            ? $"{CurrentState.clientName} đã Sẵn sàng ({CurrentState.clientDeckCount} lá)!"
            : $"{CurrentState.clientName} đã hủy Sẵn sàng.");

        BroadcastLobbyStateToClient();
        NotifyLobbyStateChanged();
    }

    private void OnReceiveLobbyState(ulong senderClientId, FastBufferReader reader)
    {
        if (!IsClientMode) return;

        LobbyStateDto state = ReadJsonMessage<LobbyStateDto>(reader);
        if (state == null) return;

        CurrentState = state;
        if (!string.IsNullOrEmpty(state.roomId6)) CurrentRoomId = state.roomId6;
        IsUsingCloudRelay = state.isCloudMode;

        MatchSessionConfig.HostPlayerName = state.hostName;
        MatchSessionConfig.ClientPlayerName = state.clientName;
        MatchSessionConfig.HostDeckCount = state.hostDeckCount;
        MatchSessionConfig.ClientDeckCount = state.clientDeckCount;

        if (state.hostReady && state.clientReady && state.hostDeckValid && state.clientDeckValid)
        {
            SetStatus("Cả 2 đã Sẵn sàng và bộ bài hợp lệ! Đang chờ Host bấm BẮT ĐẦU TRẬN...");
        }
        else
        {
            SetStatus($"Đã vào phòng ID {CurrentRoomId} ({state.connectedCount}/{MaxPlayers}). Bấm SẴN SÀNG để chuẩn bị!");
        }

        NotifyLobbyStateChanged();
    }

    private void OnReceiveMatchStarting(ulong senderClientId, FastBufferReader reader)
    {
        if (!IsClientMode) return;

        LobbyMatchStartingDto info = ReadJsonMessage<LobbyMatchStartingDto>(reader);
        if (info != null)
        {
            MatchSessionConfig.HostPlayerName = info.hostName;
            MatchSessionConfig.ClientPlayerName = info.clientName;
            MatchSessionConfig.HostDeckCount = info.hostDeckCount;
            MatchSessionConfig.ClientDeckCount = info.clientDeckCount;
        }

        MatchSessionConfig.CurrentMode = MatchMode.Multiplayer1v1;
        MatchSessionConfig.LocalIsHost = false;
        IsTransitioningToBattle = true;

        SetStatus("Host đã bắt đầu trận đấu! Đang chuyển sang BattleScene...");
        NotifyLobbyStateChanged();
    }

    private void BroadcastLobbyStateToClient()
    {
        if (!IsHostMode || connectedClientId == ulong.MaxValue) return;
        if (networkManager == null || !networkManager.IsServer) return;

        CurrentState.roomId6 = CurrentRoomId;
        CurrentState.hasPassword = !string.IsNullOrEmpty(CurrentRoomPassword);
        CurrentState.isCloudMode = IsUsingCloudRelay;

        SendJsonMessage(MSG_LOBBY_STATE, connectedClientId, CurrentState);
    }

    private void SendJsonMessage<T>(string messageName, ulong targetClientId, T payload)
    {
        if (networkManager == null || networkManager.CustomMessagingManager == null) return;

        string json = JsonUtility.ToJson(payload);
        byte[] bytes = Encoding.UTF8.GetBytes(json);

        using (FastBufferWriter writer = new FastBufferWriter(sizeof(int) + bytes.Length, Allocator.Temp))
        {
            writer.WriteValueSafe(bytes.Length);
            if (bytes.Length > 0)
            {
                writer.WriteBytesSafe(bytes, bytes.Length);
            }

            networkManager.CustomMessagingManager.SendNamedMessage(
                messageName,
                targetClientId,
                writer,
                NetworkDelivery.ReliableFragmentedSequenced
            );
        }
    }

    private T ReadJsonMessage<T>(FastBufferReader reader) where T : class
    {
        try
        {
            reader.ReadValueSafe(out int length);
            if (length < 0 || length > 65536) return null;

            byte[] bytes = length > 0 ? new byte[length] : Array.Empty<byte>();
            if (length > 0)
            {
                reader.ReadBytesSafe(ref bytes, length);
            }

            string json = Encoding.UTF8.GetString(bytes);
            return JsonUtility.FromJson<T>(json);
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[MultiplayerLobby] Lỗi đọc message: {ex.Message}");
            return null;
        }
    }

    private void SetStatus(string message)
    {
        LastStatusMessage = message;
        Debug.Log($"[MultiplayerLobby] {message}");
        OnStatusChanged?.Invoke(message);
    }

    private void NotifyLobbyStateChanged()
    {
        OnLobbyStateChanged?.Invoke(CurrentState);
    }

    public static string GetLocalLanIPv4()
    {
        try
        {
            IPHostEntry host = Dns.GetHostEntry(Dns.GetHostName());
            foreach (IPAddress ip in host.AddressList)
            {
                if (ip.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(ip))
                {
                    return ip.ToString();
                }
            }
        }
        catch
        {
        }
        return "127.0.0.1";
    }
}
