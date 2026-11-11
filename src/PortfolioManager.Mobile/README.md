# Phone app

A .NET MAUI app for Android (iOS later, from the same code). It signs in through the site and has four tabs:

- **Dashboard**: total value, the latest day's move, gain, the biggest holdings and payouts expected in the next 90 days.
- **Holdings**: every holding with its value and gain. Tap one (here or on the dashboard) for its price chart (3 months,
  1 year or 5 years), units held, value and the year's range.
- **Payouts**: dividends, distributions and interest for the last 12 months, this financial year or last, with franking
  credits. The financial year is 1 July to 30 June.
- **More**: lock and privacy settings, a button to open the full site, and sign out.

Privacy: with "Lock the app" on (the default) the phone's fingerprint, face or PIN is asked for on opening and after a
minute away. "Hide in recent apps" (on by default) blanks the app in the app switcher and, on Android, blocks
screenshots. The last figures loaded are kept on the phone, encrypted with a key in the phone's secure storage, and shown
with an "Offline copy" note when there's no signal; signing out deletes them. Nothing can be changed offline.

- `src/PortfolioManager.Mobile.Client`: sign-in (OAuth with PKCE) and the `/api/v1` calls. Plain .NET, in the main
  solution and covered by `tests/PortfolioManager.Tests/MobileClientTests.cs`.
- `src/PortfolioManager.Mobile`: the screens. Kept out of `PortfolioManager.slnx` so the site builds without the phone
  tools.

## Try it on an Android phone

**With Obtainium (gets updates too):** in Obtainium tap **Add app**, paste
`https://github.com/tonydarcy/share-and-rental-portfolio-manager` and add it. Obtainium installs the newest
"Android app" release and offers each new one as an update.

**By hand:** open the repository's **Releases** page, download the newest `share-portfolio-manager-<version>.apk`
and open it on the phone. Android asks you to allow installs from that app the first time ("Install unknown apps").

Then open **Share Portfolio**, check the site address, tap **Sign in** and sign in on the site's page as usual. The
site asks whether to let the app in; choose **Allow**.

The app's key appears on the site's **API keys** page as "Share Portfolio Manager phone app". **Sign out** in the app
forgets the key on the phone; delete it on that page too if the phone is lost.

## Releases and the signing key

The **Android app** workflow builds an APK for every change to the app. On `main` it also publishes a GitHub Release
(tag `android-v0.1.<run number>`), but only when the signing key is set, because Android only installs an update
signed with the same key as the app already on the phone. The key lives in two repository secrets
(Settings > Secrets and variables > Actions):

- `ANDROID_KEYSTORE_BASE64`: the keystore file (alias `spm`), base64 encoded.
- `ANDROID_KEY_PASSWORD`: its password.

Keep a private backup of the keystore and password: if they are lost, everyone has to uninstall and reinstall the
app. Without the secrets, builds use a throwaway key and no Release is made. To make a new key:

```
keytool -genkeypair -keystore app.keystore -alias spm -keyalg RSA -keysize 4096 -validity 10000 -dname "CN=Share Portfolio Manager"
base64 -w0 app.keystore
```

## Build it yourself

```
dotnet workload install maui-android
dotnet build src/PortfolioManager.Mobile -t:InstallAndroidDependencies -f net10.0-android -p:AcceptAndroidSDKLicenses=True
dotnet build src/PortfolioManager.Mobile -f net10.0-android -t:Run     # with a phone plugged in (USB debugging on) or an emulator
```

To test against a copy of the site on your PC from the Android emulator, use the site address `http://10.0.2.2:<port>`.
Plain http only works in Debug builds; the Release APK (the one GitHub builds) is https only.
