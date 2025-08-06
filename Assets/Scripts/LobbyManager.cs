using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading.Tasks;
using Unity.Services.Lobbies;
using Unity.Services.Lobbies.Models;
using Unity.Services.Relay;
using Unity.Netcode.Transports.UTP;
using Unity.XR.CoreUtils.Bindings.Variables;
using Unity.Services.Authentication;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace XRMultiplayer
{
    public class LobbyManager : MonoBehaviour
    {
        public const string k_JoinCodeKeyIdentifier  = "j";
        public const string k_RegionKeyIdentifier    = "r";
        public const string k_BuildIdKeyIdentifier   = "b";
        public const string k_SceneKeyIdentifier     = "s";
        public const string k_EditorKeyIdentifier    = "e";

        static bool s_HideEditorInLobbies;

        [Tooltip("Evita unirse a salas de otra escena.")]
        public bool allowDifferentScenes = false;

        [Tooltip("Oculta las salas creadas en Editor en los builds.")]
        public bool hideEditorFromLobby = false;

        public Action<string> OnLobbyFailed;

        public Lobby connectedLobby { get; private set; }

        UnityTransport m_Transport;
        Coroutine      m_HeartBeatRoutine;

        public static IReadOnlyBindableVariable<string> status => m_Status;
        static BindableVariable<string> m_Status = new("");

        const string k_DebugPrepend = "<color=#EC0CFA>[Lobby Manager]</color> ";

        void Awake()
        {
            m_Transport = FindFirstObjectByType<UnityTransport>();
            if (!Application.isEditor)
                hideEditorFromLobby = false;
            s_HideEditorInLobbies = hideEditorFromLobby;
        }

        public async Task<Lobby> QuickJoinLobby()
        {
            m_Status.Value = "Buscando salas existentes...";
            Utils.Log($"{k_DebugPrepend}{m_Status.Value}");
            try
            {
                var lobby = await LobbyService.Instance.QuickJoinLobbyAsync(GetQuickJoinFilterOptions());
                await SetupRelay(lobby);
                ConnectedToLobby(lobby);
                return lobby;
            }
            catch
            {
                m_Status.Value = "No hay salas disponibles. Creando una nueva...";
                Utils.Log($"{k_DebugPrepend}{m_Status.Value}");
                return await CreateLobby();
            }
        }

        public async Task<Lobby> JoinLobby(Lobby lobby = null, string roomCode = null)
        {
            try
            {
                if (roomCode != null)
                    lobby = await LobbyService.Instance.JoinLobbyByCodeAsync(roomCode);
                else if (lobby != null)
                    lobby = await LobbyService.Instance.JoinLobbyByIdAsync(lobby.Id);

                await SetupRelay(lobby);
                ConnectedToLobby(lobby);
                return lobby;
            }
            catch (LobbyServiceException e)
            {
                var msg = e.Message;
                if (msg.Contains("Rate limit", StringComparison.InvariantCultureIgnoreCase))
                    msg = "Límite de peticiones excedido.";
                else if (msg.Contains("Lobby not found", StringComparison.InvariantCultureIgnoreCase))
                    msg = "No se encontró la sala.";
                OnLobbyFailed?.Invoke(msg);
                return null;
            }
        }

        public async Task<Lobby> CreateLobby(string roomName = null, bool isPrivate = false, int playerCount = XRINetworkGameManager.maxPlayers)
        {
            try
            {
                m_Status.Value = "Creando Relay...";
                var alloc = await RelayService.Instance.CreateAllocationAsync(playerCount);

                m_Status.Value = "Generando código de unión...";
                var joinCode = await RelayService.Instance.GetJoinCodeAsync(alloc.AllocationId);

                var options = new CreateLobbyOptions
                {
                    Data = new Dictionary<string, DataObject>()
                    {
                        { k_JoinCodeKeyIdentifier, new DataObject(DataObject.VisibilityOptions.Public, joinCode) },
                        { k_RegionKeyIdentifier,   new DataObject(DataObject.VisibilityOptions.Public, alloc.Region) },
                        { k_BuildIdKeyIdentifier,  new DataObject(DataObject.VisibilityOptions.Public, Application.version, DataObject.IndexOptions.S1) },
                        { k_SceneKeyIdentifier,    new DataObject(DataObject.VisibilityOptions.Public, SceneManager.GetActiveScene().name, DataObject.IndexOptions.S2) },
                        { k_EditorKeyIdentifier,   new DataObject(DataObject.VisibilityOptions.Public, hideEditorFromLobby.ToString(), DataObject.IndexOptions.S3) },
                    },
                    IsPrivate = isPrivate
                };

                var lobbyName = string.IsNullOrEmpty(roomName)
                    ? $"{XRINetworkGameManager.LocalPlayerName.Value}'s Room"
                    : roomName;

                var lobby = await LobbyService.Instance.CreateLobbyAsync(lobbyName, playerCount, options);

                if (m_HeartBeatRoutine != null) StopCoroutine(m_HeartBeatRoutine);
                m_HeartBeatRoutine = StartCoroutine(HeartbeatCoroutine(lobby.Id));

                m_Transport.SetHostRelayData(
                    alloc.RelayServer.IpV4,
                    (ushort)alloc.RelayServer.Port,
                    alloc.AllocationIdBytes,
                    alloc.Key,
                    alloc.ConnectionData
                );

                ConnectedToLobby(lobby);
                return lobby;
            }
            catch (Exception)
            {
                OnLobbyFailed?.Invoke("Error al crear la sala.");
                return null;
            }
        }

        async Task SetupRelay(Lobby lobby)
        {
            m_Status.Value = "Conectando al Relay...";
            var alloc = await RelayService.Instance.JoinAllocationAsync(lobby.Data[k_JoinCodeKeyIdentifier].Value);
            m_Transport.SetClientRelayData(
                alloc.RelayServer.IpV4,
                (ushort)alloc.RelayServer.Port,
                alloc.AllocationIdBytes,
                alloc.Key,
                alloc.ConnectionData,
                alloc.HostConnectionData
            );
        }

        QuickJoinLobbyOptions GetQuickJoinFilterOptions()
        {
            var versionFilter = new QueryFilter(QueryFilter.FieldOptions.S1, Application.version, QueryFilter.OpOptions.EQ);
            var sceneFilter   = new QueryFilter(QueryFilter.FieldOptions.S2, SceneManager.GetActiveScene().name, QueryFilter.OpOptions.EQ);
            var editorFilter  = new QueryFilter(QueryFilter.FieldOptions.S3, hideEditorFromLobby.ToString(), QueryFilter.OpOptions.EQ);

            return new QuickJoinLobbyOptions
            {
                Filter = new List<QueryFilter> { versionFilter, sceneFilter, editorFilter }
            };
        }

        IEnumerator HeartbeatCoroutine(string lobbyId, float interval = 15f)
        {
            var wait = new WaitForSecondsRealtime(interval);
            while (true)
            {
                LobbyService.Instance.SendHeartbeatPingAsync(lobbyId);
                yield return wait;
            }
        }

        void ConnectedToLobby(Lobby lobby)
        {
            connectedLobby = lobby;
            m_Status.Value = "Conectado a la sala.";
        }

        public async Task<bool> RemovePlayerFromLobby(string playerId)
        {
            if (m_HeartBeatRoutine != null) StopCoroutine(m_HeartBeatRoutine);

            if (connectedLobby == null) return false;

            if (connectedLobby.HostId == playerId)
            {
                await LobbyService.Instance.DeleteLobbyAsync(connectedLobby.Id);
                connectedLobby = null;
            }
            else
            {
                await LobbyService.Instance.RemovePlayerAsync(connectedLobby.Id, playerId);
                connectedLobby = null;
            }
            return true;
        }

        // ——— Novedad: Métodos para PlayerOptions.cs ———

        /// <summary>Cambia el nombre de la sala actual.</summary>
        public async Task UpdateLobbyName(string newName)
        {
            if (connectedLobby == null) return;
            try
            {
                var opts = new UpdateLobbyOptions { Name = newName };
                await LobbyService.Instance.UpdateLobbyAsync(connectedLobby.Id, opts);
                connectedLobby = await LobbyService.Instance.GetLobbyAsync(connectedLobby.Id);
            }
            catch (Exception e)
            {
                Utils.LogError($"{k_DebugPrepend}Error actualizando nombre: {e}");
            }
        }

        /// <summary>Cambia la privacidad de la sala actual.</summary>
        public async Task UpdateRoomPrivacy(bool makePrivate)
        {
            if (connectedLobby == null) return;
            try
            {
                var opts = new UpdateLobbyOptions { IsPrivate = makePrivate };
                await LobbyService.Instance.UpdateLobbyAsync(connectedLobby.Id, opts);
                connectedLobby = await LobbyService.Instance.GetLobbyAsync(connectedLobby.Id);
            }
            catch (Exception e)
            {
                Utils.LogError($"{k_DebugPrepend}Error cambiando privacidad: {e}");
            }
        }
    }
}
