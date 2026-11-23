Add-Type -AssemblyName System.Drawing

function New-RoundedRectPath([float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $p.AddArc($x, $y, $r, $r, 180, 90)
    $p.AddArc($x + $w - $r, $y, $r, $r, 270, 90)
    $p.AddArc($x + $w - $r, $y + $h - $r, $r, $r, 0, 90)
    $p.AddArc($x, $y + $h - $r, $r, $r, 90, 90)
    $p.CloseFigure()
    return $p
}

function New-MailIcon([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.Clear([System.Drawing.Color]::Transparent)

    # --- Фон: скруглённый квадрат с градиентом ---
    $rect = New-Object System.Drawing.Rectangle(0, 0, $size, $size)
    $bgPath = New-RoundedRectPath 0 0 $size $size ($size * 0.20)
    $bgBrush = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        $rect,
        [System.Drawing.Color]::FromArgb(255, 96, 165, 250),   # #60A5FA
        [System.Drawing.Color]::FromArgb(255, 29, 78, 216),    # #1D4ED8
        135)
    $g.FillPath($bgBrush, $bgPath)

    # --- Конверт ---
    $ex = $size * 0.18
    $ey = $size * 0.27
    $ew = $size * 0.64
    $eh = $size * 0.46
    $envPath = New-RoundedRectPath $ex $ey $ew $eh ($size * 0.05)
    $g.FillPath([System.Drawing.Brushes]::White, $envPath)

    # --- Клапан конверта (светло-голубой треугольник) ---
    $flap = New-Object System.Drawing.Drawing2D.GraphicsPath
    $flap.AddLines(@(
        (New-Object System.Drawing.PointF(($ex), ($ey))),
        (New-Object System.Drawing.PointF(($ex + $ew), ($ey))),
        (New-Object System.Drawing.PointF(($ex + $ew / 2), ($ey + $eh * 0.62)))
    ))
    $flap.CloseFigure()
    $flapBrush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 219, 234, 254)) # #DBEAFE
    $g.FillPath($flapBrush, $flap)

    # --- Стрелка «перенос» (тёмно-синяя) ---
    $arrowColor = [System.Drawing.Color]::FromArgb(255, 30, 64, 175) # #1E40AF
    $pen = New-Object System.Drawing.Pen($arrowColor, ($size * 0.085))
    $pen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $pen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $ay = $ey + $eh * 0.78
    $x0 = $ex + $ew * 0.12
    $x1 = $ex + $ew * 0.62
    $g.DrawLine($pen, $x0, $ay, $x1, $ay)

    # Наконечник стрелки
    $head = New-Object System.Drawing.Drawing2D.GraphicsPath
    $tipX = $ex + $ew * 0.86
    $baseX = $ex + $ew * 0.64
    $half = $size * 0.085
    $head.AddLines(@(
        (New-Object System.Drawing.PointF($baseX, ($ay - $half - $size * 0.02))),
        (New-Object System.Drawing.PointF($tipX, ($ay))),
        (New-Object System.Drawing.PointF($baseX, ($ay + $half + $size * 0.02)))
    ))
    $head.CloseFigure()
    $headBrush = New-Object System.Drawing.SolidBrush($arrowColor)
    $g.FillPath($headBrush, $head)

    $head.Dispose(); $headBrush.Dispose(); $pen.Dispose()
    $flap.Dispose(); $flapBrush.Dispose(); $envPath.Dispose()
    $bgBrush.Dispose(); $bgPath.Dispose(); $g.Dispose()

    return $bmp
}

$outDir = 'C:\Users\a.meri\mail-migration\MailMigrator'

# PNG для предпросмотра (256)
$png = New-MailIcon 256
$png.Save("$outDir\app-icon-256.png", [System.Drawing.Imaging.ImageFormat]::Png)
$png.Dispose()

# Собрать ICO из размеров 16/32/48/64/128/256
$sizes = @(16, 32, 48, 64, 128, 256)
$images = @()
foreach ($s in $sizes) {
    $b = New-MailIcon $s
    $ms = New-Object System.IO.MemoryStream
    $b.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $images += ,@($s, $ms.ToArray())
    $ms.Dispose(); $b.Dispose()
}

$icoPath = "$outDir\app.ico"
$fs = [System.IO.File]::Create($icoPath)
$bw = New-Object System.IO.BinaryWriter($fs)

# ICONDIR
$bw.Write([uint16]0)            # reserved
$bw.Write([uint16]1)            # type = icon
$bw.Write([uint16]$images.Count) # count

# Размер заголовка: 6 + 16*N
$offset = 6 + 16 * $images.Count
$entries = @()
foreach ($img in $images) {
    $s = $img[0]; $data = $img[1]
    $entries += ,@($s, $data, $offset)
    $offset += $data.Length
}

foreach ($e in $entries) {
    $s = $e[0]; $data = $e[1]; $off = $e[2]
    $bw.Write([byte]($(if ($s -ge 256) { 0 } else { $s })))  # width
    $bw.Write([byte]($(if ($s -ge 256) { 0 } else { $s })))  # height
    $bw.Write([byte]0)   # palette
    $bw.Write([byte]0)   # reserved
    $bw.Write([uint16]1) # planes
    $bw.Write([uint16]32)# bpp
    $bw.Write([uint32]$data.Length)
    $bw.Write([uint32]$off)
}
foreach ($e in $entries) {
    $bw.Write($e[1])
}
$bw.Flush(); $bw.Close(); $fs.Close()

Write-Host "OK: app.ico and app-icon-256.png written"
