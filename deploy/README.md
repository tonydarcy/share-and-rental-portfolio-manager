# Putting a new version live (Windows server with IIS)

One double-click does the whole update. You only step in to run new database scripts in SSMS.

## Every update

1. Sign in to the server (Remote Desktop).
2. Double-click **`Deploy.cmd`** in the `deploy` folder of the code that GitHub Desktop pulls (in GitHub Desktop,
   **Repository > Show in Explorer** opens it). A copy anywhere else won't work.
   (Tip: right-click it, **Send to > Desktop (create shortcut)**, and use the desktop icon from then on.)
3. If it lists database scripts, open each one in SSMS, pick the live database, run it, top to bottom.
   Then type **Y**. If there are none, it carries straight on.
4. Wait for the green **Done** line (about two minutes). Press any key to close the window.

That's it. You don't need to open GitHub Desktop, copy files or recycle the app pool.

## What it does for you

1. Gets the latest code from GitHub (the same as Fetch origin then Pull in GitHub Desktop).
2. Lists the database scripts added since your last update, and waits until you've run them.
3. Builds the site. If the build fails, it stops and the live site is not touched.
4. Backs up the live site folder to `_deploy\wwwroot-backup-<date-time>` (keeps the newest five).
5. Shows visitors an "Updating the site" page for the few seconds the copy takes, copies the new files in,
   and never replaces `appsettings.Production.json`. Only the built site is copied, never the code.
   If the copy fails, it puts the backup back by itself.
6. Opens the home page to check the site answers.

## The first time

It asks three questions and remembers the answers in `deploy\deploy-settings.json` (kept out of git):

- **The live site's folder**, the one with `appsettings.Production.json` in it. It finds this itself if it can;
  press Enter to accept.
- **Where backups go**, normally `_deploy` next to the site's folder. Press Enter to accept.
- **The site address**, to check it at the end.

It can't tell which database scripts you've already run the first time, so it shows the newest five.
Run any you haven't. From then on it remembers which version is live and lists only new ones.

To change an answer, delete `deploy\deploy-settings.json` and run `Deploy.cmd` again.

## If something goes wrong

- **It stops with a red STOPPED line**: it says why and what to do. Unless it says otherwise, the live site
  hasn't changed.
- **The site is broken after an update**: double-click **`Rollback.cmd`**. It puts back the newest backup.
  It does not undo database scripts; they only add things, so the older version keeps working with them.
- **The error itself** is in Event Viewer > Windows Logs > Application.

## Later: fully automatic

GitHub can run this for you whenever a pull request is merged (a GitHub Actions "self-hosted runner" on the
server). It saves the double-click but means a merge goes live straight away, so it isn't set up for now.
