# Developer-only Windows PowerShell fixture. Uses synthetic data in an unshown form.
param([Parameter(Mandatory=$true)][string]$ExecutablePath, [Parameter(Mandatory=$true)][string]$OutputDirectory, [switch]$Comparison, [switch]$OcrReplay)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
[Windows.Forms.Application]::EnableVisualStyles()
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$assembly = [Reflection.Assembly]::LoadFrom([IO.Path]::GetFullPath($ExecutablePath))
$instance = [Reflection.BindingFlags]'Instance,NonPublic'
$static = [Reflection.BindingFlags]'Static,NonPublic'
$formType = $assembly.GetType('RemoteMonitorSlave.SlaveForm', $true)
$observationType = $assembly.GetType('RemoteMonitorLink.PowerSiObservation', $true)
$form = $formType.GetConstructor($instance, $null, [type[]]@([string]), $null).Invoke([object[]]@([string](Join-Path $OutputDirectory 'state')))
try {
    # Synthetic excerpt only. No actual PowerSI/model, network, Show, activation or input.
    $excerpt = "[Output recent logs - synthetic layout fixture]`r`nCapture UTC 2026-09-11 00:00:00`r`n" +
        "Simulation resumed.`r`nAFS Current Frequency ( MHz ) = 38.000`r`nSimulation completed."
    $observation = $observationType.GetMethod('VisionLogExcerpt', $static).Invoke($null, [object[]]@($excerpt, [DateTime]::UtcNow))
    $observationType.GetField('LocalEvidence', $instance).SetValue($observation, $excerpt)
    if ($Comparison) {
        $bufferType = $assembly.GetType('RemoteMonitorLink.OutputBufferResult', $true)
        $buffer = [Activator]::CreateInstance($bufferType, $true)
        $bufferType.GetField('Text', $instance).SetValue($buffer, "FIRST LINE - synthetic full buffer`r`n" + $excerpt + "`r`nLAST LINE - retained outside the OCR tail")
        $bufferType.GetField('Code', $instance).SetValue($buffer, 'BUFFER_READ')
        $bufferType.GetField('Method', $instance).SetValue($buffer, 'NATIVE_WM_GETTEXT')
        $bufferType.GetField('Detail', $instance).SetValue($buffer, 'B1|2|1|1|0')
        $fixture = New-Object Drawing.Bitmap 900, 700
        $graphics = [Drawing.Graphics]::FromImage($fixture)
        $memory = New-Object IO.MemoryStream
        try {
            $graphics.Clear([Drawing.Color]::LightGray)
            $graphics.DrawString('FULL WORKBENCH - SYNTHETIC FIXTURE', $form.Font, [Drawing.Brushes]::Black, 10, 10)
            $graphics.FillRectangle([Drawing.Brushes]::Black, 100, 300, 700, 300)
            $graphics.DrawString("Output - SYNTHETIC FIXTURE`r`n" + $excerpt, $form.Font, [Drawing.Brushes]::White, 110, 310)
            $fixture.Save($memory, [Drawing.Imaging.ImageFormat]::Png)
            $frameType = $assembly.GetType('RemoteMonitorLink.PowerSiFrame', $true)
            $frame = [Activator]::CreateInstance($frameType, $true)
            $frameType.GetField('Png', $instance).SetValue($frame, $memory.ToArray())
            $frameType.GetField('PixelSize', $instance).SetValue($frame, $fixture.Size)
            $cropType = $assembly.GetType('RemoteMonitorLink.PowerSiScreenCapture', $true)
            $cropBounds = New-Object Drawing.Rectangle 100, 300, 700, 300
            $crop = $cropType.GetMethod('Crop', $static).Invoke($null, [object[]]@($frame, $cropBounds.PSObject.BaseObject))
            $observationType.GetField('LocalImage', $instance).SetValue($observation, $crop)
            $observationType.GetField('LocalPaneImage', $instance).SetValue($observation, $crop)
            $observationType.GetField('LocalSuggestedImage', $instance).SetValue($observation, $crop)
            $observationType.GetField('LocalFrame', $instance).SetValue($observation, $frame)
            $observationType.GetField('LocalSampleId', $instance).SetValue($observation, ('A' * 64))
            $observationType.GetField('LocalOcrSampleId', $instance).SetValue($observation, ('B' * 64))
            $modelType = $assembly.GetType('RemoteMonitorLink.LocalVisionModel', $true)
            $model = [Activator]::CreateInstance($modelType, $true)
            $modelType.GetField('Id', $instance).SetValue($model, 'gemma-4-e4b-it-synthetic')
            $modelType.GetField('Quantization', $instance).SetValue($model, 'Q6_K')
            $observationType.GetField('LocalModelInfo', $instance).SetValue($observation, $model)
            if ($OcrReplay) { $observationType.GetField('LocalVisionMode', $instance).SetValue($observation, 'OCR_ONLY') }
            $observationType.GetField('LocalFullImage', $instance).SetValue($observation, $memory.ToArray())
            $observationType.GetField('LocalCaptureInfo', $instance).SetValue($observation, 'Source 900x700 / crop x=100, y=300, 700x300 / accuracy unverified (synthetic)')
        } finally { $graphics.Dispose(); $fixture.Dispose(); $memory.Dispose() }
        $formType.GetMethod('RenderOutputBuffer', $instance).Invoke($form, [object[]]@($buffer, $observation, $null))
        $dialog = $formType.GetMethod('CreateComparisonDialog', $instance).Invoke($form, $null)
    } else { $formType.GetMethod('RenderPowerSi', $instance).Invoke($form, [object[]]@($observation)) }
    if ($Comparison) { $renderForm = $dialog } else { $renderForm = $form }
    $renderForm.CreateControl()
    $bitmap = New-Object Drawing.Bitmap $renderForm.Width, $renderForm.Height
    try {
        $renderForm.DrawToBitmap($bitmap, (New-Object Drawing.Rectangle 0, 0, $renderForm.Width, $renderForm.Height))
        function Paint-Children($parent) {
          foreach ($control in $parent.Controls) {
            $null = $control.Handle
            $origin = $control.PointToScreen([Drawing.Point]::Empty)
            $rectangle = New-Object Drawing.Rectangle ($origin.X - $renderForm.Left), ($origin.Y - $renderForm.Top), $control.Width, $control.Height
            $control.DrawToBitmap($bitmap, $rectangle)
            Paint-Children $control
          }
        }
        Paint-Children $renderForm
        if ($Comparison) {
            $columns = $dialog.Controls[0]
            $right = $columns.Panel2.Controls[0]
            $selector = @($right.Panel1.Controls | Where-Object { $_ -is [Windows.Forms.ComboBox] })[0]
            if ($selector.SelectedIndex -ne 0 -or -not $selector.Text.StartsWith('Output')) { throw 'Crop selection lost after creating native handles.' }
            Write-Output ('Native combo state: ' + $selector.SelectedIndex + ' / ' + $selector.Text)
            $runs = @($dialog.Controls | Where-Object { $_ -is [Windows.Forms.ComboBox] })[0]
            if ($runs.SelectedIndex -ne 0 -or -not $runs.Text.Contains('gemma-4-e4b-it-synthetic')) { throw 'Model run selection lost.' }
            Write-Output ('Native run state: ' + $runs.Text)
        }
        $bitmap.Save((Join-Path $OutputDirectory 'slave-excerpt.png'), [Drawing.Imaging.ImageFormat]::Png)
    } finally { $bitmap.Dispose() }
    Write-Output "Offscreen excerpt rendered: $($form.Text)"
} finally { if ($Comparison -and $null -ne $dialog) { $dialog.Dispose() }; $form.Dispose() }
