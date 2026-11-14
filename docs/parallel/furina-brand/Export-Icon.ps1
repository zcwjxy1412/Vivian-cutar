# Deterministic format/size export of the native generated source; no drawing edits.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$source = [Drawing.Bitmap]::new((Join-Path $PSScriptRoot 'icon-source.png'))
$asset = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../../src/VIvian.App/Assets/Furina'))
$frames = [Collections.Generic.List[byte[]]]::new()
$sizes = @(16,24,32,48,256)
try {
 foreach ($size in $sizes) {
  $bitmap = [Drawing.Bitmap]::new($size,$size,[Drawing.Imaging.PixelFormat]::Format32bppArgb)
  $graphics = [Drawing.Graphics]::FromImage($bitmap)
  $attributes = [Drawing.Imaging.ImageAttributes]::new()
  $stream = [IO.MemoryStream]::new()
  try {
   $graphics.Clear([Drawing.Color]::Transparent)
   $graphics.CompositingMode = [Drawing.Drawing2D.CompositingMode]::SourceCopy
   $graphics.CompositingQuality = [Drawing.Drawing2D.CompositingQuality]::HighQuality
   $graphics.InterpolationMode = [Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
   $graphics.PixelOffsetMode = [Drawing.Drawing2D.PixelOffsetMode]::HighQuality
   $attributes.SetWrapMode([Drawing.Drawing2D.WrapMode]::TileFlipXY)
   $graphics.DrawImage($source,[Drawing.Rectangle]::new(0,0,$size,$size),0,0,$source.Width,$source.Height,[Drawing.GraphicsUnit]::Pixel,$attributes)
   $bitmap.Save($stream,[Drawing.Imaging.ImageFormat]::Png)
   $bytes=$stream.ToArray(); $frames.Add($bytes)
   [IO.File]::WriteAllBytes((Join-Path $PSScriptRoot "furina-app-$size.png"),$bytes)
   if ($size -eq 256) { [IO.File]::WriteAllBytes((Join-Path $asset 'furina-app.png'),$bytes) }
  } finally { $stream.Dispose(); $attributes.Dispose(); $graphics.Dispose(); $bitmap.Dispose() }
 }
 $output=[IO.File]::Create((Join-Path $asset 'furina-app.ico'))
 $writer=[IO.BinaryWriter]::new($output)
 try {
  $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$sizes.Count)
  $offset=6+16*$sizes.Count
  for($i=0;$i -lt $sizes.Count;$i++) {
   $dimension=if($sizes[$i] -eq 256){0}else{$sizes[$i]}
   $writer.Write([byte]$dimension); $writer.Write([byte]$dimension); $writer.Write([byte]0); $writer.Write([byte]0)
   $writer.Write([uint16]1); $writer.Write([uint16]32); $writer.Write([uint32]$frames[$i].Length); $writer.Write([uint32]$offset)
   $offset += $frames[$i].Length
  }
  foreach($frame in $frames){$writer.Write($frame)}
 } finally { $writer.Dispose(); $output.Dispose() }
} finally { $source.Dispose() }
