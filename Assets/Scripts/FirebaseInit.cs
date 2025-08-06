using UnityEngine;
using Firebase;
using Firebase.Auth;
using Firebase.Database;
using Firebase.Extensions;

public class FirebaseInit : MonoBehaviour
{
    // Firebase Auth instance
    public static FirebaseAuth Auth;
    // Root reference for Realtime Database
    public static DatabaseReference RootRef;
    // Internal storage of the anonymous user ID
    public static string UserId;
    // Exposed alias for code clarity
    public static string FirebaseUserId => UserId;
    // Indicates whether Firebase is initialized and user is signed in
    public static bool IsReady { get; private set; }

    // Event fired once Firebase Auth + Database are ready to use
    public static event System.Action OnFirebaseReady;

    [Header("Opcional: si el google-services.json no se carga, pon aquí tu URL de Realtime DB")]
    [SerializeField] private string fallbackDatabaseUrl = "https://avatarsvr-ddb1c-default-rtdb.firebaseio.com/"; // ej: "https://<tu-proyecto>-default-rtdb.firebaseio.com/"

    private void Awake()
    {
        // Verificar y resolver dependencias de Firebase
        FirebaseApp.CheckAndFixDependenciesAsync().ContinueWithOnMainThread(task =>
        {
            if (task.Result != DependencyStatus.Available)
            {
                Debug.LogError("Firebase no está disponible: " + task.Result);
                return;
            }

            // Inicializar la aplicación Firebase por defecto
            var app = FirebaseApp.DefaultInstance;

            // Inicializar Auth
            Auth = FirebaseAuth.DefaultInstance;

            // Inicializar Realtime Database (usar fallback si se proporciona)
            if (!string.IsNullOrEmpty(fallbackDatabaseUrl))
            {
                var db = FirebaseDatabase.GetInstance(app, fallbackDatabaseUrl);
                RootRef = db.RootReference;
            }
            else
            {
                RootRef = FirebaseDatabase.DefaultInstance.RootReference;
            }

            // Autenticación anónima en Firebase
            Auth.SignInAnonymouslyAsync().ContinueWithOnMainThread(authTask =>
            {
                if (authTask.IsCanceled || authTask.IsFaulted)
                {
                    Debug.LogError("Error autenticando anónimamente: " + authTask.Exception);
                    return;
                }

                // Guardar el UID y notificar que Firebase está listo
                UserId = Auth.CurrentUser.UserId;
                Debug.Log($"Firebase listo. Usuario anónimo con UID: {UserId}");

                IsReady = true;
                OnFirebaseReady?.Invoke();
            });
        });
    }
}
