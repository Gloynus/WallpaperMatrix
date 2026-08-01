# Privacy policy

Wallpaper Matrix does not collect telemetry, analytics, advertising
identifiers or crash reports. The application contains no feature that sends
settings, diagnostics, process information, screenshots, image contents or
personal data to the author or another service.

**This program will not transfer any information to other networked systems
unless specifically requested by the user or the person installing or
operating it.**

The application reads only local information required for its functions:

- monitor identities, geometry and desktop-window state;
- user-selected image files and folders;
- installed font information;
- foreground and full-screen application state for the optional game pause;
- keyboard and mouse idle time for the optional “АТАКА СИСТЕМЫ” mode.

When “АТАКА СИСТЕМЫ” starts, the application takes one composited screenshot
of the current virtual desktop. It keeps only a reduced luminance map of
visible top-level window regions; Explorer wallpaper surfaces and Wallpaper
Matrix output windows are excluded. The full-colour pixels are cleared from
memory immediately after this map is created. Neither the screenshot nor the
map is written to disk, added to diagnostics, retained after the attack, or
sent over a network. The map is used only to let newly arriving glyphs briefly
describe the visible interface before returning to the current playlist image.

Settings, playlists, presets and diagnostics are stored locally in the
`OperatorData` folder next to the executable. Source images are never modified.
The diagnostic log may contain local file paths and should be reviewed before
it is shared publicly.

Wallpaper Matrix opens an image in the Windows-associated viewer or copies the
author’s email address only after an explicit user action. External
applications have their own privacy behaviour.

The optional autostart setting creates or removes only the current user’s
Wallpaper Matrix entry in the Windows `Run` registry key. No service, driver,
scheduled task or system-wide component is installed.
