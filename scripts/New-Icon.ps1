#Requires -Version 7.0
[CmdletBinding()]
param([string]$Source='')
$ErrorActionPreference='Stop'
Add-Type -AssemblyName System.Drawing.Common
$root=Split-Path $PSScriptRoot -Parent
if(!$Source){$Source=Join-Path $root 'src\MathGPT.App\Assets\MathGPT.png'}
$output=Join-Path $root 'src\MathGPT.App\Assets\MathGPT.ico'
$original=[Drawing.Image]::FromFile($Source)
$frames=[Collections.Generic.List[byte[]]]::new()
$sizes=@(16,20,24,32,40,48,64,128,256)
try{
 foreach($size in $sizes){
  $bitmap=[Drawing.Bitmap]::new($size,$size,[Drawing.Imaging.PixelFormat]::Format32bppArgb)
  $graphics=[Drawing.Graphics]::FromImage($bitmap)
  $stream=[IO.MemoryStream]::new()
  try{
   $graphics.InterpolationMode=[Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
   $graphics.PixelOffsetMode=[Drawing.Drawing2D.PixelOffsetMode]::HighQuality
   $graphics.DrawImage($original,[Drawing.Rectangle]::new(0,0,$size,$size))
   $bitmap.Save($stream,[Drawing.Imaging.ImageFormat]::Png)
   $frames.Add($stream.ToArray())
  }finally{$stream.Dispose();$graphics.Dispose();$bitmap.Dispose()}
 }
 $file=[IO.File]::Create($output)
 $writer=[IO.BinaryWriter]::new($file)
 try{
  $writer.Write([uint16]0);$writer.Write([uint16]1);$writer.Write([uint16]$sizes.Count)
  $offset=6+16*$sizes.Count
  for($i=0;$i-lt$sizes.Count;$i++){
   $dimension=if($sizes[$i]-eq256){0}else{$sizes[$i]}
   $writer.Write([byte]$dimension);$writer.Write([byte]$dimension);$writer.Write([byte]0);$writer.Write([byte]0)
   $writer.Write([uint16]1);$writer.Write([uint16]32)
   $writer.Write([uint32]$frames[$i].Length);$writer.Write([uint32]$offset)
   $offset+=$frames[$i].Length
  }
  foreach($frame in $frames){$writer.Write($frame)}
 }finally{$writer.Dispose();$file.Dispose()}
}finally{$original.Dispose()}
Write-Output ('ICON_CREATED: '+$sizes.Count+' sizes; original artwork preserved.')
