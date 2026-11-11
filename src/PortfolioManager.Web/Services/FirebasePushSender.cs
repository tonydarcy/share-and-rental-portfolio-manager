using FirebaseAdmin;
using FirebaseAdmin.Messaging;
using Google.Apis.Auth.OAuth2;
using PortfolioManager.Core.Alerts;

namespace PortfolioManager.Web.Services;

/// <summary>Push settings: the Firebase service account key file (JSON) downloaded from the Firebase console.</summary>
public sealed class PushSettings
{
    public string? FirebaseCredentialsFile { get; set; }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(FirebaseCredentialsFile);
}

/// <summary>
/// Sends notifications through Firebase Cloud Messaging, which reaches Android phones directly and iPhones through Apple's
/// push service (once the Apple key is added in the Firebase console). Uses Google's FirebaseAdmin package (Apache-2.0).
/// </summary>
public sealed class FirebasePushSender : IPushSender
{
    private readonly FirebaseMessaging messaging;

    public FirebasePushSender(PushSettings settings)
    {
        var credential = CredentialFactory.FromFile<ServiceAccountCredential>(settings.FirebaseCredentialsFile!).ToGoogleCredential()
            .CreateScoped("https://www.googleapis.com/auth/firebase.messaging");
        var app = FirebaseApp.GetInstance("push") ?? FirebaseApp.Create(new AppOptions { Credential = credential }, "push");
        messaging = FirebaseMessaging.GetMessaging(app);
    }

    public bool IsConfigured => true;

    public async Task<IReadOnlyList<string>> SendAsync(IReadOnlyList<string> tokens, PushMessage message, CancellationToken ct = default)
    {
        if (tokens.Count == 0)
            return [];
        var gone = new List<string>();
        // Firebase takes up to 500 tokens a call
        foreach (var batch in tokens.Chunk(500))
        {
            // FirebaseAdmin 3.7 marks registration tokens as deprecated in favour of installation ids (Fid), but tokens
            // are what the Android and iOS Firebase libraries hand the app today, and Firebase still accepts them
#pragma warning disable CS0618
            var response = await messaging.SendEachAsync(batch.Select(token => new Message
            {
                Token = token,
                Notification = new Notification { Title = message.Title, Body = message.Body },
                Data = message.Data,
            }), ct);
#pragma warning restore CS0618
            for (var i = 0; i < response.Responses.Count; i++)
                if (response.Responses[i].Exception?.MessagingErrorCode is MessagingErrorCode.Unregistered or MessagingErrorCode.InvalidArgument)
                    gone.Add(batch[i]);
        }
        return gone;
    }
}
