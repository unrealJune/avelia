# terminal/vendor

xterm.js and its fit + WebGL addons live here at build/run time. They are **not
committed** — run the vendoring script after cloning (and after bumping the
pinned versions):

```powershell
./scripts/vendor-xterm.ps1
```

This populates `xterm.js`, `xterm.css`, `addon-fit.js`, and `addon-webgl.js`,
which `terminal.html` loads and the `.csproj` packages as `Content`. See backend
plan chunk **B-7**.
