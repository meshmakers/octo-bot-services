# Windows Utility Commands: octo-bot-services

This project is developed on Windows. Here are the key Windows commands you'll need.

## File System Navigation

```powershell
# List directory contents
dir                           # List files and folders
dir /s                        # List recursively
dir /b                        # Bare format (names only)
ls                            # PowerShell alias for Get-ChildItem

# Change directory
cd C:\dev\meshmakers\octo-bot-services
cd src\BotServices           # Relative path
cd ..                        # Up one level
cd ..\..                     # Up two levels

# Current directory
cd                           # Shows current directory
pwd                          # PowerShell: Print working directory

# Create directory
mkdir new-folder
md new-folder

# Remove directory
rmdir folder-name            # Empty directories only
rmdir /s /q folder-name      # Recursive, quiet
rm -r folder-name            # PowerShell recursive remove
```

## File Operations

```powershell
# View file contents
type filename.txt            # CMD: Display file
cat filename.txt             # PowerShell: Display file
more filename.txt            # Paginated view

# Copy files
copy source.txt dest.txt     # CMD
cp source.txt dest.txt       # PowerShell

# Move/rename files
move old.txt new.txt         # CMD
mv old.txt new.txt           # PowerShell
ren old.txt new.txt          # Rename

# Delete files
del filename.txt             # CMD
rm filename.txt              # PowerShell
```

## Search Operations

```powershell
# Find files
dir /s /b *.csproj           # CMD: Find all .csproj files recursively
where /r . *.sln             # Find all .sln files from current directory
Get-ChildItem -Recurse -Filter "*.cs"  # PowerShell: Find .cs files

# Search in files (grep equivalent)
findstr /s /i "search term" *.cs       # CMD: Search in all .cs files
Select-String -Path *.cs -Pattern "search term"  # PowerShell grep

# Find in files recursively
findstr /s /i /n "className" *.cs      # With line numbers
```

## Process Management

```powershell
# List running processes
tasklist                     # All processes
tasklist | findstr dotnet    # Filter for dotnet processes
Get-Process                  # PowerShell

# Kill a process
taskkill /PID 1234           # Kill by process ID
taskkill /IM dotnet.exe /F   # Kill by image name (force)
Stop-Process -Id 1234        # PowerShell
```

## Environment Variables

```powershell
# View environment variables
set                          # CMD: Show all
echo %PATH%                  # CMD: Show specific variable
$env:PATH                    # PowerShell: Show PATH
Get-ChildItem Env:           # PowerShell: Show all

# Set environment variable (current session)
set VAR_NAME=value           # CMD
$env:VAR_NAME = "value"      # PowerShell
```

## Network & Connectivity

```powershell
# Check network connectivity
ping google.com
ping nuget.mm.cloud

# Test port connectivity
Test-NetConnection -ComputerName nuget.mm.cloud -Port 443  # PowerShell

# View IP configuration
ipconfig
ipconfig /all
```

## Git Commands (Windows)

```powershell
# Git is the same on Windows as Unix
git status
git log --oneline
git branch
git checkout -b dev/feature-name
git add .
git commit -m "AB#123: Description"
git push origin dev/feature-name
git pull origin main
```

## PowerShell Specific

```powershell
# Check PowerShell version
$PSVersionTable

# Get command help
Get-Help <cmdlet-name>
Get-Help Get-ChildItem -Examples

# Measure command execution time
Measure-Command { dotnet build Octo.Bots.sln --configuration DebugL }

# Pipeline operations (similar to Unix)
Get-ChildItem *.cs | Select-String "class"
dir | Where-Object { $_.Length -gt 1MB }
```

## Path Separators

**Important**: Windows uses backslashes (`\`) for paths, but many tools accept forward slashes (`/`).

```powershell
# Both work in most contexts:
cd src\BotServices           # Windows native
cd src/BotServices           # Also works in most tools

# In .csproj files and code, use:
..\..\..\nuget               # Relative path in XML
../../nuget                  # Also valid in many contexts
```

## Command Prompt vs PowerShell

**This project works with both**, but PowerShell is recommended for richer scripting capabilities.

### Command Prompt (CMD)
- Traditional Windows shell
- Commands: `dir`, `copy`, `move`, `del`, `findstr`

### PowerShell
- Modern Windows shell
- Commands: `Get-ChildItem` (alias: `ls`, `dir`), `Copy-Item` (alias: `cp`), `Remove-Item` (alias: `rm`)
- Object-oriented pipeline
- Better scripting capabilities

## Common Tasks

```powershell
# Clean build artifacts
rmdir /s /q bin
rmdir /s /q obj

# Or in PowerShell:
rm -r -Force bin,obj

# Find all test projects
dir /s /b *Tests.csproj

# Count lines of code
(Get-ChildItem -Recurse -Include *.cs | Select-String .).Count

# Open current directory in Explorer
explorer .
start .

# Open file in default editor
start Program.cs
```

## .NET CLI Commands

The .NET CLI works identically on Windows:

```bash
dotnet --version
dotnet build
dotnet test
dotnet run
dotnet clean
dotnet restore
```

## IDE Integration

```powershell
# Open solution in Visual Studio
start Octo.Bots.sln

# Open in VS Code
code .

# Open in JetBrains Rider
rider Octo.Bots.sln
```
