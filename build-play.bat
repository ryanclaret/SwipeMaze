@echo off
rem Builds the web version of Swipe Maze as a static site:
rem   dist\play-site\       the folder to upload (Netlify "drag and drop", Cloudflare Pages "direct upload", ...)
rem   dist\SwipeMaze-web.zip  the same folder as a zip
rem Needs the .NET SDK on THIS PC only. The result is plain files for any web host.
setlocal
cd /d "%~dp0"

if exist dist\play rmdir /s /q dist\play
if exist dist\play-site rmdir /s /q dist\play-site
if exist dist\SwipeMaze-web.zip del /q dist\SwipeMaze-web.zip

echo === Publishing the web game ===
dotnet publish SwipeMaze.Play\SwipeMaze.Play.csproj -c Release -o dist\play || goto :fail

echo === Assembling the site ===
xcopy /e /i /q /y dist\play\wwwroot dist\play-site >nul || goto :fail
rem The host compresses files itself; the pre-compressed copies only add upload size.
del /s /q dist\play-site\*.br dist\play-site\*.gz >nul 2>&1
rem Not Compress-Archive: in Windows PowerShell it writes backslashes into the zip's file names, and a Linux host
rem (Cloudflare, Netlify) then sees "css\app.css" as one file in the top folder instead of app.css inside css/.
powershell -NoProfile -Command "Add-Type -AssemblyName System.IO.Compression.FileSystem; $s = Join-Path (Get-Location) 'dist\play-site'; $z = Join-Path (Get-Location) 'dist\SwipeMaze-web.zip'; $zip = [System.IO.Compression.ZipFile]::Open($z, 'Create'); Get-ChildItem $s -Recurse -File | ForEach-Object { $name = $_.FullName.Substring($s.Length + 1).Replace('\', '/'); [void][System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $_.FullName, $name, 'Optimal') }; $zip.Dispose()" || goto :fail

echo.
echo Done:
echo   Upload the folder  dist\play-site   (or the zip  dist\SwipeMaze-web.zip)
exit /b 0

:fail
echo.
echo BUILD FAILED
exit /b 1
