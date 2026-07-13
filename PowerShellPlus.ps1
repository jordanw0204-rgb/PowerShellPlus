# PowerShellPlus - simple scheduler GUI
# Lets you pick a time of day; at that time Invoke-TabEnter.ps1 runs and presses
# Enter in each of your Windows Terminal tabs. Uses Windows Task Scheduler, so
# the schedule fires even when this window is closed.

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

$root       = Split-Path -Parent $MyInvocation.MyCommand.Path
$configPath = Join-Path $root 'config.json'
$corePath   = Join-Path $root 'Invoke-TabEnter.ps1'
$taskName   = 'PowerShellPlus'

# ---- config helpers ---------------------------------------------------------
function Get-Config {
    $default = [pscustomobject]@{ time = '06:35'; tabCount = 5; delayMs = 400; processName = 'WindowsTerminal' }
    if (Test-Path $configPath) {
        try { return Get-Content $configPath -Raw | ConvertFrom-Json } catch { return $default }
    }
    return $default
}

function Save-Config($time, $tabCount, $delayMs) {
    [pscustomobject]@{
        time        = $time
        tabCount    = [int]$tabCount
        delayMs     = [int]$delayMs
        processName = 'WindowsTerminal'
    } | ConvertTo-Json | Set-Content -Path $configPath -Encoding UTF8
}

# ---- scheduled task helpers -------------------------------------------------
function Get-Task { Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue }

function Set-Schedule([string]$time) {
    $action    = New-ScheduledTaskAction -Execute 'powershell.exe' `
                 -Argument "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File `"$corePath`""
    $trigger   = New-ScheduledTaskTrigger -Daily -At $time
    $principal = New-ScheduledTaskPrincipal -UserId "$env:USERDOMAIN\$env:USERNAME" -LogonType Interactive
    $settings  = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries `
                 -StartWhenAvailable -ExecutionTimeLimit (New-TimeSpan -Minutes 5)
    Register-ScheduledTask -TaskName $taskName -Action $action -Trigger $trigger `
        -Principal $principal -Settings $settings -Force | Out-Null
}

function Remove-Schedule {
    if (Get-Task) { Unregister-ScheduledTask -TaskName $taskName -Confirm:$false }
}

# ---- UI ----------------------------------------------------------------------
$form                 = New-Object System.Windows.Forms.Form
$form.Text            = 'PowerShellPlus'
$form.Size            = New-Object System.Drawing.Size(360, 320)
$form.FormBorderStyle = 'FixedSingle'
$form.MaximizeBox     = $false
$form.StartPosition   = 'CenterScreen'

$font = New-Object System.Drawing.Font('Segoe UI', 10)
$form.Font = $font

function New-Label($text, $x, $y) {
    $l = New-Object System.Windows.Forms.Label
    $l.Text = $text; $l.Location = New-Object System.Drawing.Point($x, $y); $l.AutoSize = $true
    $form.Controls.Add($l); return $l
}

New-Label 'Trigger time (daily):' 20 20 | Out-Null
$timePicker               = New-Object System.Windows.Forms.DateTimePicker
$timePicker.Format        = 'Custom'
$timePicker.CustomFormat  = 'hh:mm tt'
$timePicker.ShowUpDown    = $true
$timePicker.Location      = New-Object System.Drawing.Point(180, 16)
$timePicker.Width         = 140
$form.Controls.Add($timePicker)

New-Label 'Number of tabs:' 20 60 | Out-Null
$tabCountBox          = New-Object System.Windows.Forms.NumericUpDown
$tabCountBox.Minimum  = 1
$tabCountBox.Maximum  = 50
$tabCountBox.Location = New-Object System.Drawing.Point(180, 56)
$tabCountBox.Width    = 140
$form.Controls.Add($tabCountBox)

New-Label 'Delay between keys (ms):' 20 100 | Out-Null
$delayBox          = New-Object System.Windows.Forms.NumericUpDown
$delayBox.Minimum  = 100
$delayBox.Maximum  = 5000
$delayBox.Increment = 100
$delayBox.Location = New-Object System.Drawing.Point(180, 96)
$delayBox.Width    = 140
$form.Controls.Add($delayBox)

$statusLabel           = New-Label 'Status: ...' 20 140
$statusLabel.ForeColor = [System.Drawing.Color]::DimGray

function Update-Status {
    $t = Get-Task
    if ($t) {
        $info = Get-ScheduledTaskInfo -TaskName $taskName
        $next = if ($info.NextRunTime) { $info.NextRunTime.ToString('ddd h:mm tt') } else { 'unknown' }
        $statusLabel.Text      = "Status: SCHEDULED - next run $next"
        $statusLabel.ForeColor = [System.Drawing.Color]::ForestGreen
    } else {
        $statusLabel.Text      = 'Status: not scheduled'
        $statusLabel.ForeColor = [System.Drawing.Color]::DimGray
    }
}

function New-Button($text, $x, $y, $w) {
    $b = New-Object System.Windows.Forms.Button
    $b.Text = $text; $b.Location = New-Object System.Drawing.Point($x, $y)
    $b.Size = New-Object System.Drawing.Size($w, 34)
    $form.Controls.Add($b); return $b
}

$saveBtn   = New-Button 'Save && Schedule' 20 180 150
$removeBtn = New-Button 'Remove Schedule' 180 180 140
$testBtn   = New-Button 'Test Now (runs in 5s)' 20 224 300

$saveBtn.Add_Click({
    try {
        $time = $timePicker.Value.ToString('HH:mm')
        Save-Config $time $tabCountBox.Value $delayBox.Value
        Set-Schedule $time
        Update-Status
        [System.Windows.Forms.MessageBox]::Show("Scheduled daily at $($timePicker.Value.ToString('h:mm tt')).", 'PowerShellPlus') | Out-Null
    } catch {
        [System.Windows.Forms.MessageBox]::Show("Failed: $_", 'PowerShellPlus', 'OK', 'Error') | Out-Null
    }
})

$removeBtn.Add_Click({
    Remove-Schedule
    Update-Status
})

$testBtn.Add_Click({
    Save-Config $timePicker.Value.ToString('HH:mm') $tabCountBox.Value $delayBox.Value
    $statusLabel.Text = 'Test run starts in 5 seconds - click into your terminal!'
    $statusLabel.ForeColor = [System.Drawing.Color]::DarkOrange
    Start-Process powershell.exe -ArgumentList "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -Command `"Start-Sleep 5; & '$corePath'`""
})

# ---- load saved values -------------------------------------------------------
$cfg = Get-Config
$timePicker.Value  = [datetime]::ParseExact($cfg.time, 'HH:mm', $null)
$tabCountBox.Value = [decimal]$cfg.tabCount
$delayBox.Value    = [decimal]$cfg.delayMs
Update-Status

[void]$form.ShowDialog()
