# Builds every TaskDock deliverable into artifacts/release:
#   TaskDock.exe                       self-contained single file (no .NET install needed)
#   com.darkpixelgd.taskdock-<v>.tgz   Unity package (Package Manager → Add package from tarball)
#   taskdock-<v>.vsix                  VS Code extension (Install from VSIX)
#   taskdock-browser-extension.zip     unzip, then Load unpacked in Chrome / Edge / Brave
# Run from anywhere: powershell -ExecutionPolicy Bypass -File tools/release.ps1
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$out = Join-Path $root 'artifacts/release'
if (Test-Path $out) { Remove-Item -Recurse -Force $out }
New-Item -ItemType Directory -Force $out | Out-Null

function Run([string]$what, [scriptblock]$cmd) {
    Write-Host "== $what"
    & $cmd
    if ($LASTEXITCODE) { throw "$what failed (exit $LASTEXITCODE)" }
}

Run 'tests (.NET)' { dotnet test (Join-Path $root 'TaskDock.sln') -c Release --nologo -v q }

# Not compressed on purpose: compressed single-file assemblies are unpacked into private memory
# (110 MB vs 32 MB idle), and the plan's budget is 80 MB.
Run 'TaskDock.exe' {
    dotnet publish (Join-Path $root 'src/TaskDock.App') -c Release -r win-x64 --self-contained `
        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none `
        -o (Join-Path $root 'artifacts/publish') --nologo -v q
}
Copy-Item (Join-Path $root 'artifacts/publish/TaskDock.exe') $out

Run 'Unity package' {
    Push-Location (Join-Path $root 'adapters/unity/com.darkpixelgd.taskdock')
    try { npm pack --silent --pack-destination $out } finally { Pop-Location }
}

Run 'VS Code extension' {
    Push-Location (Join-Path $root 'adapters/vscode')
    try {
        npm ci --no-audit --no-fund --silent
        if (!$LASTEXITCODE) { npm test --silent }
        if (!$LASTEXITCODE) { npm run package --silent }
        if (!$LASTEXITCODE) { Move-Item -Force *.vsix $out }
    } finally { Pop-Location }
}

Run 'browser extension' {
    $ext = Join-Path $root 'adapters/chrome'
    node --test (Join-Path $ext 'test/state.test.mjs')
    if (!$LASTEXITCODE) {
        $files = Get-ChildItem $ext -File | Where-Object Name -ne 'README.md'
        Compress-Archive -Path $files.FullName -DestinationPath (Join-Path $out 'taskdock-browser-extension.zip')
    }
}

Write-Host "`nArtifacts in $out"
Get-ChildItem $out | Format-Table Name, @{ n = 'MB'; e = { '{0:N1}' -f ($_.Length / 1MB) } } -AutoSize
