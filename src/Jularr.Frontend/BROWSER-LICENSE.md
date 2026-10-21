# Jularr browser client

Copyright (c) 2026 Juloc.

Jularr-authored browser-side TypeScript/JavaScript, styles and browser artwork
in this directory are licensed under the GNU General Public License, version 3
only. You may redistribute and modify that browser-client code under GPLv3.
There is no warranty. The complete license is in
[GPL-3.0.txt](Modules/Speech/Runtime/licenses/GPL-3.0.txt).

This is the Owner's browser-client license decision, not a license grant for
the C# web host, Backend, Integrations, Workers, native clients or the repository
as a whole. It does not relicense third-party files, models, datasets or separate
optional engines/cores. Their original licenses and notices remain applicable;
in particular GPLv3 does not turn non-commercial model/core terms into permission
for commercial use of those assets.

The ordinary browser dependencies retain their licenses: hls.js 1.7.3
(Apache-2.0), htmx 4.0.0 (0BSD), and the optional Bergamot files (MPL-2.0).
No new combined binary is made from those engines and Speech. Optional EmulatorJS
and its separately distributed cores retain their own terms under `/emulator`;
they are not inputs to the Speech WASM build.

The optional Speech binary includes GPLv3 eSpeak NG. Its linked-runtime notices,
upstream source revisions, build inputs and Jularr's modifications are in
[Runtime/NOTICE.md](Modules/Speech/Runtime/NOTICE.md). The Speech settings expose
a readable, build-generated notice bundle, including the GPL and original
dependency notices, and a public source link. A clean build records its exact
Git source revision in that bundle; a dirty/unversioned build is marked as a
development build, not advertised as a reproducible release.

Redistributors must keep the corresponding sources available for the binary
they deliver, including these client sources, the engine sources/changes and
build instructions. An inaccessible or removed upstream link is not a substitute
for fulfilling that obligation. The public Jularr source repository is
<https://github.com/Juloc/Jularr>.
