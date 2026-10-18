# Тестовый кадр H.264

red.h264 — один синтетический красный кадр 64×64, без пользовательских данных.
Создан FFmpeg 7.0.2/libx264 (инструмент тестирования, в приложение не поставляется):

```
ffmpeg -f lavfi -i color=c=red:s=64x64:r=30 -frames:v 1 -c:v libx264 -profile:v baseline -tune zerolatency -x264-params keyint=1:bframes=0:colorprim=bt709:transfer=bt709:colormatrix=bt709 -f h264 red.h264
```

Windows-тест подаёт этот Annex B в настоящий Microsoft H.264 Decoder MFT,
проверяет размер и цвет декодированного NV12/BGRA. Это не тест физического GPU.

red-crop.h264 создан той же командой с s=66x50, чтобы проверить обрезку SPS и
строки NV12, не кратные 16 пикселям.
