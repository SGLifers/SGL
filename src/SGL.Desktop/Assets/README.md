# SGL Windows icon

`sgl-source.png` is the original 720 × 720 transparent Splitgate Lifers image supplied by the user. Its artwork and transparency are preserved. `sgl.ico` is a format conversion with 16, 24, 32, 48, 64, 128 and 256 pixel frames, using Windows DIB encoding for WPF compatibility.

The executable embeds it through `ApplicationIcon`; the shared Window style loads the same resource for the taskbar and application windows. No external icon file is needed after publishing the single-file executable.
