# Phone app

A .NET MAUI app for Android (iOS later, from the same code). Version 0.1 signs in and shows each portfolio's holdings.

- `src/PortfolioManager.Mobile.Client`: sign-in (OAuth with PKCE) and the `/api/v1` calls. Plain .NET, in the main
  solution and covered by `tests/PortfolioManager.Tests/MobileClientTests.cs`.
- `src/PortfolioManager.Mobile`: the screens. Kept out of `PortfolioManager.slnx` so the site builds without the phone
  tools.

## Try it on an Android phone

1. On GitHub, open **Actions**, then **Android app**, then the latest green run. Download **android-app** under
   Artifacts and unzip it to get `com.shareportfoliomanager.app-Signed.apk`. (Or press **Run workflow** to make a fresh one.)
2. Copy the APK to the phone (email it to yourself, or Google Drive) and open it. Android asks you to allow installs
   from that app the first time ("Install unknown apps").
3. Open **Share Portfolio**, check the site address, tap **Sign in** and sign in on the site's page as usual. The site
   asks whether to let the app in; choose **Allow**.

The app's key appears on the site's **API keys** page as "Share Portfolio Manager phone app". **Sign out** in the app
forgets the key on the phone; delete it on that page too if the phone is lost.

The test APK is signed with a throwaway key, so each new build installs over the last only if Android accepts it;
if it says "App not installed", uninstall the old one first.

## Build it yourself

```
dotnet workload install maui-android
dotnet build src/PortfolioManager.Mobile -t:InstallAndroidDependencies -f net10.0-android -p:AcceptAndroidSDKLicenses=True
dotnet build src/PortfolioManager.Mobile -f net10.0-android -t:Run     # with a phone plugged in (USB debugging on) or an emulator
```

To test against a copy of the site on your PC from the Android emulator, use the site address `http://10.0.2.2:<port>`.
