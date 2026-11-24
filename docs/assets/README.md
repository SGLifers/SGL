# Setup guide screenshot sources

- `extract-dialog.png`, `smartscreen-more-info.png` and `smartscreen-run-anyway.png` are the user's SGL installation screenshots supplied on November 22, 2026. These replace the previous reference images.
- `extract-menu.jpg` is the existing Windows 11 Extract All reference from [Inclusive Technology: How to Unzip Files in Windows 10 and 11](https://support.inclusive.com/article/how-to-unzip-a-zip-file-in-windows-10-and-11).
  - Original: https://tawk.link/66d816c5ea492f34bc0d83ff/kb/attachments/4AaGVMBeMf.jpg

The four `*-annotated.svg` files embed the original screenshot bytes unchanged and add a red rectangle and arrow over the relevant button. Regenerate them with `node scripts/annotate-setup-screenshots.mjs`. The self-contained SVGs work in both the online and offline guides.

`sgl-editor.png` and `sgl-history.png` show SGL with demonstration data. `protection-history.png` is the original Microsoft Support reference screenshot, restored to the guide at the user's request. It is an example rather than an actual SGL detection. Source: https://support.microsoft.com/en-us/windows/security/windows-security/protection-history-in-the-windows-security-app

The editor and history images were refreshed on November 23, 2026 using offscreen renders of the current WPF interface with isolated demonstration saves. History opens inside Gamemodes; neither image includes real player saves or personal paths.

The current editor/history examples also show the Installed, Community and My uploads navigation added during the local/cloud library consolidation.

Current screenshots also reflect removal of gamemode draft controls and content share-link actions.
