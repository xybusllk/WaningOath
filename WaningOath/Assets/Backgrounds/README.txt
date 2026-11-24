Scene backgrounds live here (ASCII file names, read by Scripts/Backgrounds.cs).

The client asks for exactly these, and nothing else:

    site14-hill-dawn.png      prologue S1  - hill behind Site 14, morning mist
    site14-hill-ridge.png     prologue S1b - looking down at the facility cluster
    battle-site14-hill.png    prologue S7  - the roadside thicket (battle)
    site14-canteen.png        prologue S8  - the canteen

Naming follows <place>-<slug>[-<variant>].png, per the art plan (00 sec.6.2 / 02 sec.2.3).
A missing file is NOT an error: the screen simply gets no background layer, and the
counts are reported at startup as [SMOKE] assets backgrounds=<loaded> missing=<missing>
so tools/godot-smoke.ps1 can assert on them.

DO NOT rename a file once the client references it: the name is a string literal in
Backgrounds.Requested, and a typo means a SILENTLY missing image.

This file is ASCII-only on purpose (the source folder for the art itself uses CJK
names; that is fine, but anything under Assets/ is read by code).