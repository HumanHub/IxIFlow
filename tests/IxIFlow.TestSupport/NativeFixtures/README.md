# Prebuilt native activity fixtures

Tests embed these images; no compiler or child process is needed at test time.
Native execution cases run on win-x64 and linux-x64 and explicitly skip other
RIDs. The osx-arm64 image is used only by the existing cross-RID selection test.
`fixture.c` returns 42. `primary.c` imports `fixture_helper` from `helper.c`.
The images have no C runtime dependencies.

Exact build commands used on Windows, from this directory (all intermediate
files stay in the task's TEMP directory):

```powershell
$clPath = 'C:/Program Files/Microsoft Visual Studio/18/Community/VC/Tools/MSVC/14.51.36231/bin/Hostx64/x64/cl.exe'
$linkPath = 'C:/Program Files/Microsoft Visual Studio/18/Community/VC/Tools/MSVC/14.51.36231/bin/Hostx64/x64/link.exe'
$clangPath = 'C:/Program Files/dotnet/packs/Microsoft.NET.Runtime.Emscripten.3.1.56.Sdk.win-x64/10.0.12/tools/bin/clang.exe'
$lldPath = 'C:/Program Files/dotnet/packs/Microsoft.NET.Runtime.Emscripten.3.1.56.Sdk.win-x64/10.0.12/tools/bin/wasm-ld.exe'
$taskScratch = Join-Path $env:TEMP 'ixiflow-ap-r2-20261020'
New-Item -ItemType Directory -Force -Path $taskScratch,win-x64,linux-x64,osx-arm64 | Out-Null
& $clPath /nologo /c /O2 /GS- fixture.c ('/Fo' + (Join-Path $taskScratch 'fixture.obj'))
& $clPath /nologo /c /O2 /GS- helper.c ('/Fo' + (Join-Path $taskScratch 'helper.obj'))
& $clPath /nologo /c /O2 /GS- primary.c ('/Fo' + (Join-Path $taskScratch 'primary.obj'))
& $linkPath /NOLOGO /DLL /NOENTRY /NODEFAULTLIB /MACHINE:X64 /Brepro /EXPORT:fixture_value (Join-Path $taskScratch 'fixture.obj') /OUT:win-x64/fixture_native.dll ('/IMPLIB:' + (Join-Path $taskScratch 'fixture.lib'))
& $linkPath /NOLOGO /DLL /NOENTRY /NODEFAULTLIB /MACHINE:X64 /Brepro /EXPORT:fixture_helper (Join-Path $taskScratch 'helper.obj') /OUT:win-x64/fixture_helper.dll ('/IMPLIB:' + (Join-Path $taskScratch 'helper.lib'))
& $linkPath /NOLOGO /DLL /NOENTRY /NODEFAULTLIB /MACHINE:X64 /Brepro /EXPORT:fixture_primary (Join-Path $taskScratch 'primary.obj') (Join-Path $taskScratch 'helper.lib') /OUT:win-x64/fixture_primary.dll ('/IMPLIB:' + (Join-Path $taskScratch 'primary.lib'))
foreach ($file in @('fixture','helper','primary')) {
    & $clangPath --target=x86_64-linux-gnu -c -O2 -fPIC -fno-stack-protector ($file + '.c') -o (Join-Path $taskScratch ($file + '.o'))
}
& $lldPath -flavor gnu -shared --build-id=none -soname fixture_native.so -o linux-x64/fixture_native.so (Join-Path $taskScratch 'fixture.o')
& $lldPath -flavor gnu -shared --build-id=none -soname fixture_helper.so -o linux-x64/fixture_helper.so (Join-Path $taskScratch 'helper.o')
& $lldPath -flavor gnu -shared --build-id=none -soname fixture_primary.so -o linux-x64/fixture_primary.so (Join-Path $taskScratch 'primary.o') linux-x64/fixture_helper.so
& $clangPath --target=arm64-apple-macos11 -c -O2 -fPIC -fno-stack-protector fixture.c -o (Join-Path $taskScratch 'fixture-mac.o')
& $lldPath -flavor darwin -dylib -arch arm64 -platform_version macos 11.0 11.0 -no_uuid -install_name '@loader_path/libfixture.dylib' -o osx-arm64/fixture_native.dylib (Join-Path $taskScratch 'fixture-mac.o')
```

The executable named `wasm-ld.exe` is the installed LLVM 19.1.0 universal LLD
driver: `-flavor gnu` selects its ELF linker and `-flavor darwin` its Mach-O
linker. The Windows compiler/linker is MSVC 14.51.36231. No downloads were used.
