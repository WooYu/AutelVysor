# Build status — Autel Vysor v0.5.0

## Completed in the current development environment

- WPF/XAML XML parsing.
- XAML event-handler consistency checks.
- Lightweight C# structural/brace validation.
- Version/resource consistency review.
- Release script consistency review.
- `FileDescription=Autel` and `ProductName=Autel Vysor` build gates remain enabled.
- Windows release now includes an executable `--self-test` gate after `dotnet publish`.

## Not executable in the current container

The current container does not provide `dotnet`, PowerShell (`pwsh`) or Wine, and outbound binary downloads are unavailable. Therefore a genuine WPF/Windows compile cannot be run inside this container.

The included GitHub Actions workflow or any Windows 10/11 x64 machine with .NET 8 can perform the real compile and release validation:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\build-release.ps1
```

A successful Windows build must produce:

```text
dist\AutelVysor\AutelVysor.exe
dist\AutelVysor-v0.5.0-windows-x64.zip
dist\AutelVysor-Setup.exe
```

The build is considered successful only when the executable metadata checks and `AutelVysor.exe --self-test` both pass.
