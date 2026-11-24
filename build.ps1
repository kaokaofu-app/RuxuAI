$ErrorActionPreference = 'Stop'
Push-Location -LiteralPath $PSScriptRoot
$guard = New-Object System.Threading.Mutex($false, 'Local\JiyueAI-Build-512267f1')
$acquired = $false
try {
    try { $acquired = $guard.WaitOne(0) }
    catch [System.Threading.AbandonedMutexException] { $acquired = $true }
    if (!$acquired) { throw 'Another build is already publishing this app. Retry when it finishes.' }
    $framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
    $compiler = Join-Path $framework 'csc.exe'
    $refs = foreach ($assembly in 'UIAutomationClient','UIAutomationTypes','WindowsBase','PresentationCore','PresentationFramework','WindowsFormsIntegration') {
        '/reference:' + (Join-Path $framework "WPF\$assembly.dll")
    }
    $metadata = Join-Path $env:WINDIR 'System32\WinMetadata'
    foreach ($component in 'Foundation','Media','Storage','Graphics','Globalization') {
        $refs += '/reference:' + (Join-Path $metadata "Windows.$component.winmd")
    }
    $id = [Guid]::NewGuid().ToString('N')
    $appFile = ([char]0x5982).ToString() + [char]0x8BB8 + 'AI.exe'
    $stagingDirectory = Join-Path $PSScriptRoot ".build-$id"
    [IO.Directory]::CreateDirectory($stagingDirectory) | Out-Null
    $staged = Join-Path $stagingDirectory $appFile
    $refs += '/reference:Accessibility.dll'
    $refs += '/resource:HomeRail.xaml,Jiyue.HomeRail.xaml'
    $refs += '/resource:ScrollChrome.xaml,Jiyue.ScrollChrome.xaml'
    foreach ($icon in 'screenshot','document','arrow_down','history','settings','copy','arrow_clockwise','pin','pin_off','more_horizontal','stop','dismiss','search','delete','text_font_size') {
        $refs += "/resource:FluentIcons\$icon.svg,Fluent.$icon.svg"
    }
    $sources = @('Program.cs','Home.cs','Windows.cs','AppAppearance.cs','QuestionComposer.cs','JumpToLatestButton.cs','ReplyWaitingDots.cs','CapturePopup.cs','ReplyView.cs','MarkdownReply.cs','ConversationStore.cs',
        'HistoryWindow.cs','SourceCard.cs','CaptureSource.cs','SettingsDialog.cs','Theme.cs','Motion.cs','Toast.cs','IconButton.cs','Hotkeys.cs','ReaderPreferences.cs','TrayRenderer.cs',
        'Snipper.cs','VisualSelection.cs','PageReader.cs','Model.cs','LocalOcr.cs','ArticleBudget.cs','LongArticleImage.cs','GlassButton.cs','CaptureStatus.cs','ImageImport.cs','DialogTools.cs')
    & $compiler /nologo /target:winexe /platform:x64 /langversion:5 /optimize+ /warnaserror+ /codepage:65001 "/out:$staged" /win32icon:jiyue.ico /win32manifest:app.manifest /resource:SettingsView.xaml,Jiyue.SettingsView.xaml /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:System.Net.Http.dll /reference:System.Web.Extensions.dll /reference:System.Security.dll /reference:System.Runtime.dll /reference:System.Xaml.dll @refs @sources
    if ($LASTEXITCODE -ne 0) {
        [IO.File]::Delete($staged)
        throw "Build failed ($LASTEXITCODE)"
    }
    $target = Join-Path $PSScriptRoot $appFile
    $backup = Join-Path $PSScriptRoot "Jiyue.previous-$id.bak"
    try {
        if ([IO.File]::Exists($target)) { [IO.File]::Replace($staged, $target, $backup) }
        else { [IO.File]::Move($staged, $target) }
    } catch {
        $reason = $_.Exception.GetBaseException().Message
        if (![IO.File]::Exists($target) -and [IO.File]::Exists($backup)) {
            [IO.File]::Move($backup, $target)
        }
        throw "Could not publish: $reason. Keep your current session open. To retry later, close the app normally and run build.ps1 again. Staged file if present: $staged"
    }
    # Keep the immediately previous version for recovery; a running older image can stay mapped.
    Get-ChildItem -LiteralPath $PSScriptRoot -Filter 'Jiyue.previous-*.bak' -File | Where-Object {
        $_.Name -match '^Jiyue\.previous-[0-9a-f]{32}\.bak$' -and $_.FullName -ne $backup
    } | ForEach-Object {
        try { [IO.File]::Delete($_.FullName) }
        catch [System.IO.IOException] { }
        catch [System.UnauthorizedAccessException] { }
    }
    Get-Item -LiteralPath $target | Select-Object FullName,Length,LastWriteTime
} finally {
    if ($stagingDirectory -and [IO.Directory]::Exists($stagingDirectory) -and ![IO.File]::Exists($staged)) {
        [IO.Directory]::Delete($stagingDirectory, $false)
    }
    if ($acquired) { $guard.ReleaseMutex() }
    $guard.Dispose()
    Pop-Location
}
