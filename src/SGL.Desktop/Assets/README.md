# SGL Windows icon

`sgl-source.webp` is the 80 × 80 Splitgate Lifers image supplied by the user. `sgl.ico` is a deterministic format conversion with 16, 24, 32, 48, 64 and 80 pixel frames. The ICO uses Windows DIB frames for compatibility with WPF's icon decoder.

The executable embeds it through `ApplicationIcon`; the shared Window style loads the same resource for the taskbar and application windows. No external icon file is needed after publishing the single-file executable.
