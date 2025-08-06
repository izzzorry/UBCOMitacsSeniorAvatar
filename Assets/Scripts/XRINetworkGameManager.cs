using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Unity.Netcode;
using Unity.Services.Authentication;
using Unity.Services.Lobbies.Models;
using Unity.XR.CoreUtils.Bindings.Variables;
using UnityEngine;
using UnityEngine.SceneManagement;

#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.SceneManagement;
#endif

namespace XRMultiplayer
{
    [Serializable]
    public class SceneReference
    {
#if UNITY_EDITOR
        [Tooltip("Arrastra tu escena aquí desde el proyecto")]
        public SceneAsset sceneAsset;
#endif
        [HideInInspector]
        public string sceneName;

#if UNITY_EDITOR
        public void OnValidate()
        {
            if (sceneAsset != null)
                sceneName = sceneAsset.name;
        }
#endif
    }

    [RequireComponent(typeof(AuthenticationManager), typeof(LobbyManager), typeof(VoiceChatManager))]
    public class XRINetworkGameManager : NetworkBehaviour
    {
        public enum ConnectionState { None, Authenticating, Authenticated, Connecting, Connected }
        public const int maxPlayers = 20;

        // --- SERIALIZABLE SCENES LIST ---
        [Header("Configuración de Escenas")]
        [SerializeField]
        private List<SceneReference> scenes = new List<SceneReference>();

#if UNITY_EDITOR
        private void OnValidate()
        {
            foreach (var sr in scenes)
                sr.OnValidate();
        }
#endif

        // --- SINGLETON ---
        private static XRINetworkGameManager s_Instance;
        public static XRINetworkGameManager Instance => s_Instance;

        // --- IDENTIFICADORES ---
        public static ulong LocalId;
        public static string AuthenicationId;

        // --- FIREBASE ASSIGNMENTS ---
        public string FirebaseUserId { get; private set; }
        public int AssignedScene { get; private set; }
        public string AssignedLobbyCode { get; private set; }
        public int AssignedAvatarId { get; private set; }

        // --- CONNECTION STATE & LOCAL DATA ---
        public static string ConnectedRoomCode;
        public static BindableVariable<string> ConnectedRoomName = new BindableVariable<string>("");
        public static BindableVariable<string> LocalPlayerName = new BindableVariable<string>("Player");
        public static BindableVariable<Color> LocalPlayerColor = new BindableVariable<Color>(Color.white);
        private static BindableVariable<bool> m_Connected = new BindableVariable<bool>(false);
        public static IReadOnlyBindableVariable<bool> Connected => m_Connected;
        private static BindableEnum<ConnectionState> m_ConnectionState = new BindableEnum<ConnectionState>(ConnectionState.None);
        public static IReadOnlyBindableVariable<ConnectionState> CurrentConnectionState => m_ConnectionState;

        // --- MANAGERS ---
        [SerializeField] private AuthenticationManager m_AuthenticationManager;
        [SerializeField] private LobbyManager m_LobbyManager;
        [SerializeField] private VoiceChatManager m_VoiceChatManager;
        [SerializeField] private bool m_AutoConnectOnLobbyJoin = true;

        // --- EVENTS ---
        public Action<ulong, bool> playerStateChanged;
        public Action<string> connectionUpdated;
        public Action<string> connectionFailedAction;

        private readonly List<ulong> m_CurrentPlayerIDs = new List<ulong>();
        private const string k_DebugPrepend = "<color=#FAC00C>[NetworkGameManager]</color> ";

        private async void Awake()
        {
            // Singleton enforcement
            if (s_Instance != null && s_Instance != this)
            {
                Destroy(gameObject);
                return;
            }
            s_Instance = this;
            DontDestroyOnLoad(gameObject);

            // Verify dependencies
            if (!TryGetComponent(out m_LobbyManager) || !TryGetComponent(out m_AuthenticationManager) || !TryGetComponent(out m_VoiceChatManager))
            {
                Utils.Log(k_DebugPrepend + "Missing managers, disabling.");
                enabled = false;
                return;
            }
            m_LobbyManager.OnLobbyFailed += ConnectionFailed;

            // Initial state
            m_Connected.Value = false;
            m_ConnectionState.Value = ConnectionState.Authenticating;

            await InitializeAsync();
        }

        private async Task InitializeAsync()
        {
            // 1. Authenticate with UGS
            bool signed = await m_AuthenticationManager.Authenticate();
            if (!signed)
            {
                ConnectionFailed("Authentication failed.");
                return;
            }

            // 1.1 Get Firebase UID
            FirebaseUserId = AuthenticationService.Instance.PlayerId;
            AuthenicationId = FirebaseUserId;
            m_ConnectionState.Value = ConnectionState.Authenticated;
            connectionUpdated?.Invoke("Authenticated");

            // 2. Load player assignments
            await RetrieveAssignmentsFromDatabase();
            await SaveAssignmentsToDatabase();

            // 3. Optionally save scenes dictionary (only once)
            await SaveScenesDictionaryAsync();

            // 4. Auto-join logic
            if (m_AutoConnectOnLobbyJoin)
            {
                if (!string.IsNullOrEmpty(AssignedLobbyCode))
                    JoinLobbyByCode(AssignedLobbyCode);
                else
                    QuickJoinLobby();
            }
        }

        private async Task RetrieveAssignmentsFromDatabase()
        {
            var uid = FirebaseUserId;
            var root = FirebaseInit.RootRef;
            try
            {
                // Scene index
                var sceneSnap = await root.Child("players").Child(uid).Child("scene").GetValueAsync();
                if (sceneSnap.Exists && int.TryParse(sceneSnap.Value?.ToString(), out var idx))
                    AssignedScene = idx;
                else
                {
                    Utils.LogWarning($"{k_DebugPrepend}Scene missing, defaulting to 0");
                    AssignedScene = 0;
                }
                // Room code
                var codeSnap = await root.Child("players").Child(uid).Child("room").GetValueAsync();
                AssignedLobbyCode = codeSnap.Value?.ToString() ?? string.Empty;
                if (string.IsNullOrEmpty(AssignedLobbyCode))
                    Utils.LogWarning($"{k_DebugPrepend}Room code missing");
                // Avatar id
                var avatarSnap = await root.Child("players").Child(uid).Child("avatar").GetValueAsync();
                if (avatarSnap.Exists && int.TryParse(avatarSnap.Value?.ToString(), out var aid))
                    AssignedAvatarId = aid;
                else
                {
                    Utils.LogWarning($"{k_DebugPrepend}Avatar id missing, default 0");
                    AssignedAvatarId = 0;
                }
                connectionUpdated?.Invoke($"Loaded: scene={AssignedScene}, room={AssignedLobbyCode}, avatar={AssignedAvatarId}");
            }
            catch (Exception e)
            {
                Utils.LogError($"{k_DebugPrepend}Error reading Firebase: {e}");
                ConnectionFailed(e.Message);
            }
        }

        private async Task SaveAssignmentsToDatabase()
        {
            var uid = FirebaseUserId;
            var root = FirebaseInit.RootRef;
            try
            {
                await root.Child("players").Child(uid).Child("scene").SetValueAsync(AssignedScene);
                await root.Child("players").Child(uid).Child("room").SetValueAsync(AssignedLobbyCode);
                await root.Child("players").Child(uid).Child("avatar").SetValueAsync(AssignedAvatarId);
                Utils.Log($"{k_DebugPrepend}Player assignments saved.");
            }
            catch (Exception e)
            {
                Utils.LogError($"{k_DebugPrepend}Error saving assignments: {e}");
            }
        }

        private async Task SaveScenesDictionaryAsync()
        {
            var root = FirebaseInit.RootRef;
            var dict = new Dictionary<string, object>();
            for (int i = 0; i < scenes.Count; i++)
                dict[i.ToString()] = scenes[i].sceneName;
            try
            {
                await root.Child("scenes").SetValueAsync(dict);
                Utils.Log($"{k_DebugPrepend}Scenes dictionary saved.");
            }
            catch (Exception e)
            {
                Utils.LogError($"{k_DebugPrepend}Error saving scenes: {e}");
            }
        }

        private string GetSceneName(int index)
        {
            if (index >= 0 && index < scenes.Count)
                return scenes[index].sceneName;
            return scenes.Count > 0 ? scenes[0].sceneName : string.Empty;
        }

        public override void OnNetworkSpawn()
        {
            NetworkManager.Singleton.OnClientStopped += OnLocalClientStopped;
        }

        private void OnLocalClientStopped(bool stopped)
        {
            m_Connected.Value = false;
            m_CurrentPlayerIDs.Clear();
            m_ConnectionState.Value = AuthenticationService.Instance.IsSignedIn ? ConnectionState.Authenticated : ConnectionState.None;
            connectionUpdated?.Invoke("Network stopped");
        }

        // -----------------------------
        // LOBBY & MATCHMAKING
        // -----------------------------

        public void QuickJoinLobby()
        {
            m_ConnectionState.Value = ConnectionState.Connecting;
            _ = JoinLobbyAsync();
        }

        private async Task JoinLobbyAsync()
        {
            try
            {
                var lobby = await m_LobbyManager.QuickJoinLobby();
                ConnectToLobby(lobby);
                _ = SaveAssignmentsToDatabase();
            }
            catch (Exception e)
            {
                ConnectionFailed(e.Message);
            }
        }

        public void JoinLobbyByCode(string code) => _ = JoinLobbyByCodeAsync(code);

        private async Task JoinLobbyByCodeAsync(string code)
        {
            try
            {
                var lobby = await m_LobbyManager.JoinLobby(roomCode: code);
                ConnectToLobby(lobby);
                _ = SaveAssignmentsToDatabase();
            }
            catch (Exception e)
            {
                ConnectionFailed(e.Message);
            }
        }

        private void ConnectToLobby(Lobby lobby)
        {
            if (lobby == null)
            {
                ConnectionFailed("Lobby is null.");
                return;
            }
            AssignedLobbyCode = lobby.Id;
            ConnectedRoomCode = lobby.Id;
            ConnectedRoomName.Value = lobby.Name;
            m_ConnectionState.Value = ConnectionState.Connecting;
            connectionUpdated?.Invoke($"Connecting to {lobby.Name}");

            string sceneToLoad = GetSceneName(AssignedScene);
            NetworkManager.SceneManager.LoadScene(sceneToLoad, LoadSceneMode.Single);

            if (lobby.HostId == AuthenicationId)
                NetworkManager.Singleton.StartHost();
            else
                NetworkManager.Singleton.StartClient();

            m_Connected.Value = true;
            m_ConnectionState.Value = ConnectionState.Connected;
            connectionUpdated?.Invoke("Connected");
        }

        public void ConnectionFailed(string reason)
        {
            m_ConnectionState.Value = AuthenticationService.Instance.IsSignedIn ? ConnectionState.Authenticated : ConnectionState.None;
            connectionFailedAction?.Invoke(reason);
            Utils.LogError(k_DebugPrepend + reason);
        }

        // ---------------
        // Additional API
        // ---------------

        public void Disconnect()
        {
            if (NetworkManager.Singleton.IsListening)
                NetworkManager.Singleton.Shutdown();
        }

        public LobbyManager lobbyManager => m_LobbyManager;
        public VoiceChatManager positionalVoiceChat => m_VoiceChatManager;

        // -----------------------------
        // LOBBY CREATION & CANCELLATION
        // -----------------------------

        public void CreateNewLobby() => _ = CreateNewLobbyAsync();
        public void CreateNewLobby(string roomName, bool isPrivate, int playerCount) => _ = CreateNewLobbyAsync(roomName, isPrivate, playerCount);

        private async Task CreateNewLobbyAsync()
        {
            try
            {
                var lobby = await m_LobbyManager.CreateLobby();
                ConnectToLobby(lobby);
                _ = SaveAssignmentsToDatabase();
            }
            catch (Exception e)
            {
                ConnectionFailed(e.Message);
            }
        }

        private async Task CreateNewLobbyAsync(string roomName, bool isPrivate, int playerCount)
        {
            try
            {
                var lobby = await m_LobbyManager.CreateLobby(roomName, isPrivate, playerCount);
                ConnectToLobby(lobby);
                _ = SaveAssignmentsToDatabase();
            }
            catch (Exception e)
            {
                ConnectionFailed(e.Message);
            }
        }

        public void CancelMatchmaking() => _ = CancelMatchmakingAsync();

        private async Task CancelMatchmakingAsync()
        {
            try
            {
                await m_LobbyManager.RemovePlayerFromLobby(AuthenicationId);
                m_ConnectionState.Value = ConnectionState.Authenticated;
                connectionUpdated?.Invoke("Matchmaking cancelled");
                _ = SaveAssignmentsToDatabase();
            }
            catch (Exception e)
            {
                Utils.LogError(k_DebugPrepend + $"Error cancelling matchmaking: {e}");
            }
        }

        // -----------------------------
        // PLAYER EVENTS
        // -----------------------------

        public void LocalPlayerConnected(ulong playerId)
        {
            m_Connected.Value = true;
            LocalId = playerId;
            playerStateChanged?.Invoke(playerId, true);
        }

        public void PlayerJoined(ulong playerID)
        {
            if (!m_CurrentPlayerIDs.Contains(playerID))
                m_CurrentPlayerIDs.Add(playerID);
            playerStateChanged?.Invoke(playerID, true);
        }

        public void PlayerLeft(ulong playerID)
        {
            if (m_CurrentPlayerIDs.Contains(playerID))
                m_CurrentPlayerIDs.Remove(playerID);
            playerStateChanged?.Invoke(playerID, false);
        }

        public bool GetPlayerByID(ulong id, out XRINetworkPlayer player)
        {
            foreach (var p in FindObjectsOfType<XRINetworkPlayer>())
            {
                if (p.NetworkObject.OwnerClientId == id)
                {
                    player = p;
                    return true;
                }
            }
            player = null;
            return false;
        }
    }
}
