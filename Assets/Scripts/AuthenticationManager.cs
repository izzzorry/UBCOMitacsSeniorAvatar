using System.Threading.Tasks;
using Unity.Services.Authentication;
using Unity.Services.Core;
using UnityEngine;

#if UNITY_EDITOR
#if HAS_MPPM
using Unity.Multiplayer.Playmode;
using UnityEngine.XR.Interaction.Toolkit.UI;
#endif
#if HAS_PARRELSYNC
using ParrelSync;
#endif
#endif

namespace XRMultiplayer
{
    public class AuthenticationManager : MonoBehaviour
    {
        const string k_DebugPrepend = "<color=#938FFF>[Authentication Manager]</color> ";
        const string k_PlayerArgID  = "PlayerArg";

        [SerializeField] bool m_UseCommandLineArgs = true;

        public virtual async Task<bool> Authenticate()
        {
            // 1) Inicializar UGS si hace falta
            if (UnityServices.State == ServicesInitializationState.Uninitialized)
            {
                var options = new InitializationOptions();
                string profile = "Player";

#if UNITY_EDITOR
                profile = "Editor";
#if HAS_MPPM
                profile += CheckMPPM();
#elif HAS_PARRELSYNC
                profile += CheckParrelSync();
#endif
#endif
                if (!Application.isEditor && m_UseCommandLineArgs)
                    profile += GetPlayerIDArg();

                options.SetProfile(profile);
                Utils.Log($"{k_DebugPrepend}Signing in with profile {profile}");
                await UnityServices.InitializeAsync(options);
            }

            // 2) Hacer Sign-in anónimo
            if (!AuthenticationService.Instance.IsAuthorized)
                await AuthenticationService.Instance.SignInAnonymouslyAsync();

            // 3) Cachear el UGS PlayerId
            XRINetworkGameManager.AuthenicationId = AuthenticationService.Instance.PlayerId;
            Utils.Log($"{k_DebugPrepend}UGS signed in. PlayerId: {XRINetworkGameManager.AuthenicationId}");

            // 4) Esperar a que FirebaseInit dispare OnFirebaseReady
            if (!FirebaseInit.IsReady)
            {
                var tcs = new TaskCompletionSource<bool>();
                System.Action handler = null;
                handler = () =>
                {
                    FirebaseInit.OnFirebaseReady -= handler;
                    tcs.SetResult(true);
                };
                FirebaseInit.OnFirebaseReady += handler;
                Utils.Log($"{k_DebugPrepend}Waiting for Firebase to be ready...");
                await tcs.Task;
            }

            // 5) Ya que FirebaseInit está listo, podemos leer FirebaseInit.FirebaseUserId directamente
            Utils.Log($"{k_DebugPrepend}Firebase ready. UID: {FirebaseInit.FirebaseUserId}");

            return UnityServices.State == ServicesInitializationState.Initialized
                   && FirebaseInit.IsReady;
        }

        public static bool IsAuthenticated()
        {
            try
            {
                return AuthenticationService.Instance.IsSignedIn;
            }
            catch (System.Exception e)
            {
                Utils.Log($"{k_DebugPrepend}Error checking AuthenticationService: {e}");
                return false;
            }
        }

        string GetPlayerIDArg()
        {
            string result = "";
            foreach (var arg in System.Environment.GetCommandLineArgs())
            {
                if (arg.ToLower().Contains(k_PlayerArgID.ToLower()))
                {
                    var parts = arg.Split(':');
                    if (parts.Length > 1)
                        result += parts[1];
                }
            }
            return result;
        }

#if UNITY_EDITOR
#if HAS_MPPM
        string CheckMPPM()
        {
            Utils.Log($"{k_DebugPrepend}MPPM Found");
            var tags = CurrentPlayer.ReadOnlyTags();
            return tags.Length > 0 ? tags[0] : "";
        }
#endif

#if HAS_PARRELSYNC
        string CheckParrelSync()
        {
            Utils.Log($"{k_DebugPrepend}ParrelSync Found");
            return ClonesManager.IsClone() ? ClonesManager.GetArgument() : "";
        }
#endif
#endif
    }
}
