# Основания для API и проверки

Никакие системные функции блоков Б–Г пока не реализованы. Ниже источники для
реализованного кода; реальные сигнатуры дополнительно проверяются компиляцией
Android SDK и .NET SDK в Actions. Загруженные исходники не включаются в продукт.

| Узел | Официальный источник / проверенный пример |
|---|---|
| TLS .NET | dotnet/runtime: `src/libraries/System.Net.Security/src/System/Net/Security/SslStream.cs`, blob `db932eba59193af6ce4afeda7b5a3d42fb98ff9b`; `SslClientAuthenticationOptions.cs`, blob `16700e0598dad18d39e2194becf8193ecba6bd9d` |
| Ключ Android | aosp-mirror/platform_frameworks_base: `keystore/java/android/security/keystore/KeyGenParameterSpec.java`, blob `3cff9159793916c3c67fe608b41f7bbfce7e6973` |
| Аккумулятор | тот же репозиторий: `core/java/android/os/BatteryManager.java`, blob `b63ad5f0148ee8acb90ccef1cb7f788b9e41d123` |
| WPF-UI | lepoco/wpfui: `samples/Wpf.Ui.Demo.Simple/MainWindow.xaml`, blob `ba09c4e6e358451b80cf9ec0994bef51b9987378`; пакет 4.3.0 |
| NSD | https://developer.android.com/develop/connectivity/wifi/use-nsd ; сигнатуры проверяет компилятор SDK |
| connectedDevice FGS | https://developer.android.com/develop/background-work/services/fgs/service-types#connected-device |
| DPAPI | https://learn.microsoft.com/dotnet/api/system.security.cryptography.protecteddata |
| DNS-SD | RFC 6762 / 6763: PTR запрос, SRV/A ответы; ограниченный парсер с защитой циклов и границ |
| USB | https://developer.android.com/tools/adb#forwardports |

Выбор Windows-декодера: Media Foundation (блок Б), чтобы не добавлять FFmpeg и
вторую нативную DLL. Нативная DLL будет только у виртуальной камеры (блок Г).
Для блока Г обязательны Windows-Camera/Samples/VirtualCamera, smourier/VCamSample
и заголовки установленного Windows SDK; их исследование ещё впереди.

Среда агента — Linux без Java и .NET. Локальная сборка невозможна; единственный
источник утверждения «собрано» — успешные jobs Actions, не наличие исходников.
SDK Android зафиксирован на 36; основание выбора проверено повторно ниже.

## Повторный аудит 18 октября

- Официальная страница SDK Platform release notes проверена напрямую:
  https://developer.android.com/tools/releases/platforms — верхний стабильный
  раздел в полученном документе Android 16/API 36. compileSdk/targetSdk 36
  оставлены; по случайному номеру из стороннего репозитория SDK не повышался.
- NSD registerService и четыре RegistrationListener callbacks сверены с
  официальным примером `use-nsd` (раздел Register your service).
- Для connectedDevice сверены FOREGROUND_SERVICE_CONNECTED_DEVICE и разрешённая
  предпосылка CHANGE_NETWORK_STATE в официальной таблице FGS types.
- .NET UDP SendAsync(ReadOnlyMemory<byte>, IPEndPoint, CancellationToken) сверено
  с dotnet/runtime `src/libraries/System.Net.Sockets/src/System/Net/Sockets/UDPClient.cs`.
- Одноразовые mDNS-запросы с ephemeral-порта — RFC 6762 §6.7, Legacy Unicast
  Responses. Не требуется занимать UDP 5353 рядом с системным mDNS Windows.
- Android-приём намеренно **не использует available()**: это не индикатор
  наличия зашифрованных данных. Idle timeout допустим до первого байта заголовка;
  после первого байта любой тайм-аут закрывает сеанс. Добавлены JVM unit-тесты.

- Причина ошибки Mica подтверждена **исходником версии пакета 4.3.0**:
  https://github.com/lepoco/wpfui/blob/4.3.0/src/Wpf.Ui/Controls/FluentWindow/FluentWindow.cs
  (`OnBackdropTypeChanged`, проверка `!ExtendsContentIntoTitleBar`). ui:TitleBar
  и ExtendsContentIntoTitleBar взяты из Demo.Simple этой же версии.

- Фиолетовый акцент Windows задаётся через ApplicationAccentColorManager.Apply
  (Color, ApplicationTheme), сигнатура проверена в исходнике версии 4.3.0.

- Изолированный сервер adb: флаги `-P` (порт сервера) и `kill-server` проверены
  в официальном AOSP `platform_system_core/adb/commandline.cpp`, ref android-8.1.0_r1.
  Lenscast использует 5038, не останавливая общий adb на 5037. PrepareToInstall/Exec
  сверены с официальной справкой Inno Setup `topic_scriptevents.htm` и
  `topic_isxfunc_exec.htm`. Остановка собственного adb до замены/удаления файлов
  проверяется установочным smoke-тестом.

## Исправления обнаружения 0.1.1

- UDP JoinMulticastGroup(IPAddress, IPAddress), ReceiveAsync(CancellationToken) и
  SendAsync(ReadOnlyMemory<byte>, IPEndPoint, CancellationToken) повторно сверены
  с официальным dotnet/runtime `System.Net.Sockets/.../UDPClient.cs`.
- MulticastInterface задаётся IPv4-адресом конкретного NIC. Запрос с 5353 содержит
  QU-бит класса; отдельный ephemeral-сокет принимает legacy unicast ответы.
  Основания: RFC 6762 §§5.4/6.7/15. Повтор запроса раз в секунду, не при каждом
  постороннем пакете. Пакеты PTR/SRV/A проверяются реальным UDP-тестом с потерей
  первого запроса и раздельными записями.
- `adb devices -l`, состояния device/unauthorized/offline, общий сервер 5037 и
  Windows native backend сверены с developer.android.com/tools/adb. Не добавляем
  случайные USB-драйверы и не убиваем общий сервер для «исправления» обнаружения.
- Inno: ExtractTemporaryFile, PrepareToInstall и Exec проверены в официальной
  справке jrsoftware.org. Windows Get-Process/Path, Stop-Process и Wait-Process
  используются только для adb по точному пути этой установки, с проверкой в CI
  сохранности процесса из другой папки.
- netsh advfirewall firewall add/delete rule сверены с Microsoft Learn;
  правило только для исполняемого файла Lenscast, UDP с remoteport=5353,
  remoteip=LocalSubnet, profile=private. Глобальные настройки firewall не меняются.
- Адрес телефона: ConnectivityManager.getAllNetworks/getNetworkCapabilities/
  getLinkProperties и LinkProperties.getLinkAddresses сверены с официальным AOSP
  platform_frameworks_base, ref android-11.0.0_r1, core/java/android/net.
- Реальный APK дополнительно проверяется в Android Emulator API 30/36. Входы
  api-level/target/arch/profile/script сверены с action.yml android-emulator-runner
  v2; action закреплён SHA. Скрипт проверяет FGS, KeyStore/TLS, код и токен без
  вывода секретов. Эмулятор не доказывает работу OEM USB-драйвера или реального mDNS.

### Отдельный цикл TLS · 0.1.2

- AOSP Android 11 `KeyGenParameterSpec.Builder.setDigests`: для TLS прямо требуется
  `DIGEST_NONE`, поскольку стек вычисляет хеш самостоятельно. Это разрешение
  операции ключа, не отключение хеширования в протоколе.
  https://github.com/aosp-mirror/platform_frameworks_base/blob/android-11.0.0_r1/keystore/java/android/security/keystore/KeyGenParameterSpec.java
- Официальный Conscrypt `CryptoUpcalls.rsaSignDigestWithPrivateKey` делегирует
  подписание через Cipher.ENCRYPT_MODE + приватный RSA, PKCS1Padding/NoPadding.
  https://github.com/google/conscrypt/blob/2.5.2/common/src/main/java/org/conscrypt/CryptoUpcalls.java
- AOSP `AndroidKeyStoreRSACipherSpi`: приватный ENCRYPT_MODE переводится в
  PURPOSE_SIGN; PKCS1Padding — в подпись PKCS#1, NoPadding — в raw RSA. Для обоих
  явно добавляется KM_DIGEST_NONE. Для PSS Conscrypt использует raw RSA; требуется
  ENCRYPTION_PADDING_NONE, но PURPOSE_DECRYPT/PURPOSE_ENCRYPT не выдаются.
  https://github.com/aosp-mirror/platform_frameworks_base/blob/android-11.0.0_r1/keystore/java/android/security/keystore/AndroidKeyStoreRSACipherSpi.java
- `KeyInfo.getDigests/getEncryptionPaddings/getSignaturePaddings`,
  `KeyFactory.getInstance("RSA", "AndroidKeyStore").getKeySpec(privateKey, KeyInfo.class)`
  и константы DIGEST_NONE/SHA384, SIGNATURE_PADDING_RSA_PSS,
  ENCRYPTION_PADDING_NONE проверены по KeyInfo.java, KeyProperties.java и
  AndroidKeyStoreKeyFactorySpi.java в той же версии AOSP.
- `AndroidKeyStoreKeyPairGeneratorSpi.generateKeyPair` заменяет существующий alias;
  разрешения старого ключа неизменяемы. Замена только несовместимого ключа;
  проверка сертификата ПК не меняется. Это намеренно требует нового сопряжения
  для старого несовместимого ключа.
- CPython 3.12 Lib/ssl.py: SSLContext.minimum_version/maximum_version,
  TLSVersion.TLSv1_2 и SSLSocket.version проверены по официальному исходнику.
  Принудительный TLS 1.2 — дополнительная проверка, не замена штатной negotiation.

## Блок B · экран (0.2.0)

Официальные источники, прочитанные при реализации (копии SDK — временно .arena/b-sdk):

- Android Developers Media projection: пример registerForActivityResult /
  createScreenCaptureIntent / foreground service / getMediaProjection /
  registerCallback / createVirtualDisplay. Один токен — один display; при
  изменении геометрии resize/setSurface, не повторный createVirtualDisplay.
  https://developer.android.com/media/grow/media-projection
- Реальные сигнатуры AOSP android-16.0.0_r1: MediaProjection.java,
  MediaProjectionManager.java, MediaProjectionConfig.java (createConfigForDefaultDisplay,
  API 34 guard), VirtualDisplay.java, Display.java. Начальные размеры —
  WindowManager.maximumWindowMetrics.bounds, minSdk 30.
  https://github.com/aosp-mirror/platform_frameworks_base/tree/android-16.0.0_r1
- AOSP MediaCodec.java / MediaCodecInfo.java / MediaFormat.java: configure,
  createInputSurface, start/stop/release, dequeueOutputBuffer/BufferInfo,
  getOutputBuffer/releaseOutputBuffer/outputFormat, INFO_OUTPUT_FORMAT_CHANGED,
  BUFFER_FLAG_CODEC_CONFIG/KEY_FRAME, PARAMETER_KEY_REQUEST_SYNC_FRAME,
  CodecCapabilities.COLOR_FormatSurface, isHardwareAccelerated, videoCapabilities,
  width/heightAlignment, isSizeSupported, areSizeAndRateSupported, bitrateRange.
  KEY_BIT_RATE/FRAME_RATE/I_FRAME_INTERVAL/MAX_B_FRAMES/REPEAT_PREVIOUS_FRAME_AFTER/
  MAX_FPS_TO_ENCODER/COLOR_STANDARD/COLOR_RANGE и BT709/LIMITED сверены в исходнике.
- AOSP AccessibilityService.java/GestureDescription.java: dispatchGesture,
  GestureResultCallback, StrokeDescription(path,startTime,duration,willContinue),
  continueStroke/willContinue и GLOBAL_ACTION_BACK/HOME/RECENTS. Продолжение
  начинается точно в конце предыдущего stroke; координаты следующего сегмента
  фиксируются до callback. XML canPerformGestures=true, canRetrieveWindowContent=false.
- Microsoft H.264 decoder: вход MFMediaType_Video/MFVideoFormat_H264; NV12 output;
  ProcessInput/ProcessOutput, NEED_MORE_INPUT/STREAM_CHANGE, GetOutputAvailableType.
  https://learn.microsoft.com/windows/win32/medfound/h-264-video-decoder
- Официальный Decoder/BufferLock.h sample: QueryInterface IMF2DBuffer прежде
  обычного IMFMediaBuffer; stride и времена жизни COM. Для плотного NV12 здесь
  используется IMF2DBuffer.GetContiguousLength/ContiguousCopyTo; fallback Lock/Unlock.
  https://github.com/microsoft/Windows-classic-samples/tree/main/Samples/Win7Samples/multimedia/mediafoundation/Decoder
- Direct3D 11 decoding: DXGI Device Manager, MF_SA_D3D11_AWARE,
  MFT_MESSAGE_SET_D3D_MANAGER, ID3D10Multithread.SetMultithreadProtected.
  https://learn.microsoft.com/windows/win32/medfound/supporting-direct3d-11-video-decoding-in-media-foundation
- Каждый используемый native GUID, слот vtable и сигнатура сверены с официальными
  WinSDK mfapi.h/mfobjects.h/mftransform.h/wmcodecdsp.h/Mferror.h/d3d10.h/d3d11.h:
  CoCreateInstance(CLSID_CMSH264DecoderMFT, IID_IMFTransform), MFStartup/Shutdown,
  MFCreateMediaType/Sample/MemoryBuffer/AlignedMemoryBuffer/DXGIDeviceManager,
  D3D11CreateDevice, IMFDXGIDeviceManager.ResetDevice; IMFAttributes Get/SetUINT32,
  Get/SetUINT64, Get/SetGUID; IMFSample GetBufferByIndex/AddBuffer/Get/SetSampleTime/
  SetSampleDuration; IMFMediaBuffer Lock/Unlock/SetCurrentLength; IMFTransform
  GetAttributes/GetOutputStreamInfo/GetOutputAvailableType/SetInputType/SetOutputType/
  ProcessMessage/ProcessInput/ProcessOutput. x64 MFT_OUTPUT_DATA_BUFFER использует
  стандартное выравнивание указателей, не pack=1. MF_E_NOTACCEPTING=0xC00D36B5.
  https://github.com/microsoft/win32metadata/tree/main/generation/WinSDK/RecompiledIdlHeaders/um
- HEVC: тот же SDK MFTEnumEx, MFT_REGISTER_TYPE_INFO, MFT_CATEGORY_VIDEO_DECODER,
  флаги SYNCMFT|LOCALMFT|SORTANDFILTER, IMFActivate.ActivateObject, MFVideoFormat_HEVC.
  Все IMFActivate освобождаются, массив — CoTaskMemFree. Отсутствие codec extension
  не ошибка установки: обязательный AVC остаётся доступен.
- C# интерфейсы не создаются по догадке: узкий unsafe interop использует только
  проверенные SDK-слоты; результат проверяется настоящим MFT на двух синтетических
  Annex B, а не только компиляцией. Новый сторонний/native runtime не поставляется.

Ограничения: AVD не подтверждает драйвер GPU/OEM, 30+ минут и штатную камеру.
Надпись DXVA — запрос D3D-пути, не доказательство аппаратного выполнения каждого
кадра. Для отказа аппаратного пути предусмотрен новый software MFT и запрос IDR.

Дополнительно для SPS crop: MF_MT_MINIMUM_DISPLAY_APERTURE / MFVideoArea / MFOffset
и IMFAttributes.GetBlob (слот 15) сверены с mfapi.h/mfobjects.h. Декодируемые
размеры/stride сохраняются отдельно от видимой области; смещение UV считается
по высоте кодированного NV12, а не обрезанного изображения. В CI есть 66×50.
Android 16 SystemUI consent: официальный screen_share_dialog.xml содержит
android:id/button1; ShareToAppPermissionDialogDelegate меняет текст по выбранному
типу захвата. CI нажимает ID, не полагается на английский текст старого диалога.


## Исправление обычного тапа · 0.2.1

Официальный AOSP Android 16, не предположение о длительности StrokeDescription:
- [MotionEventInjector.java](https://github.com/aosp-mirror/platform_frameworks_base/blob/android-16.0.0_r1/services/accessibility/java/com/android/server/accessibility/MotionEventInjector.java):
  appendMoveEventIfNeeded создаёт MOVE только при изменении координат; продолжение
  не создаёт DOWN, willContinue исключает UP. injectEventsMainThread при пустом
  списке событий сообщает failure, не отпуская существующий палец. Поэтому нельзя
  прокачивать неподвижный продолжаемый штрих каждые 8 мс.
- [GestureDescription.java](https://github.com/aosp-mirror/platform_frameworks_base/blob/android-16.0.0_r1/core/java/android/accessibilityservice/GestureDescription.java):
  StrokeDescription / continueStroke / willContinue, нулевой путь для тапа допустим.
- [Handler.java](https://github.com/aosp-mirror/platform_frameworks_base/blob/android-16.0.0_r1/core/java/android/os/Handler.java):
  postDelayed(Runnable,long), removeCallbacks(Runnable) — отдельный watchdog.
- [View.java](https://github.com/aosp-mirror/platform_frameworks_base/blob/android-16.0.0_r1/core/java/android/view/View.java)
  и [MotionEvent.java](https://github.com/aosp-mirror/platform_frameworks_base/blob/android-16.0.0_r1/core/java/android/view/MotionEvent.java):
  setOnClickListener / setOnLongClickListener / setOnTouchListener,
  getActionMasked / getEventTime / getDownTime, ACTION_DOWN/MOVE/UP/CANCEL.
  Отдельная Java-цель CI передаёт touch стандартной Button (listener возвращает false),
  считает реальные callback и события, не подменяет AccessibilityService.

Цель CI: View.setId(int) и сгенерированный R.id.gesture_target. UIAutomator ищет
resource-id, не текст с AllCaps. AOSP TextView.getTextForAccessibility возвращает
mTransformed, а не исходный mText:
https://github.com/aosp-mirror/platform_frameworks_base/blob/android-16.0.0_r1/core/java/android/widget/TextView.java


## Начало C · каталог возможностей (ещё не поток)

Сверено с официальным AOSP android-16.0.0_r1:
- [CameraManager.java](https://github.com/aosp-mirror/platform_frameworks_base/blob/android-16.0.0_r1/core/java/android/hardware/camera2/CameraManager.java):
  getCameraIdList, getCameraCharacteristics; чтение не открывает устройство.
- [CameraCharacteristics.java](https://github.com/aosp-mirror/platform_frameworks_base/blob/android-16.0.0_r1/core/java/android/hardware/camera2/CameraCharacteristics.java):
  getAvailableCaptureRequestKeys, getPhysicalCameraIds (physical-only ID с API29
  не обязательно можно открыть отдельно), все используемые в CameraCatalog ключи:
  LENS_FACING, SENSOR_ORIENTATION, INFO_SUPPORTED_HARDWARE_LEVEL,
  LENS_INFO_AVAILABLE_FOCAL_LENGTHS, REQUEST_AVAILABLE_CAPABILITIES,
  SCALER_STREAM_CONFIGURATION_MAP, CONTROL_AE_AVAILABLE_TARGET_FPS_RANGES,
  CONTROL_AE/AF/AWB_AVAILABLE_MODES, CONTROL_ZOOM_RATIO_RANGE,
  LENS_INFO_MINIMUM_FOCUS_DISTANCE, CONTROL_AE_COMPENSATION_RANGE/STEP,
  CONTROL_AE/AWB_LOCK_AVAILABLE, FLASH_INFO_AVAILABLE,
  CONTROL_AVAILABLE_VIDEO_STABILIZATION_MODES, LENS_INFO_AVAILABLE_OPTICAL_STABILIZATION,
  SENSOR_INFO_SENSITIVITY_RANGE/EXPOSURE_TIME_RANGE/MAX_FRAME_DURATION.
- [StreamConfigurationMap.java](https://github.com/aosp-mirror/platform_frameworks_base/blob/android-16.0.0_r1/core/java/android/hardware/camera2/params/StreamConfigurationMap.java):
  getOutputSizes(Class), getOutputMinFrameDuration(Class,Size) с MediaCodec.class.
  Длительность 0 разрешена SDK как неизвестная (в частности без MANUAL_SENSOR).
  Metadata не доказывает поддержку session + encoder и фактический FPS: каталог
  только для кандидатов, не включён в UI и не подменяет проверку потоком.

## C · Camera2 capture/control · 0.3.0

Проверено перед реализацией по исходникам SDK Android 16:
https://github.com/aosp-mirror/platform_frameworks_base/tree/android-16.0.0_r1/core/java/android/hardware/camera2

- CameraManager.openCamera(String,Executor,StateCallback); CameraDevice StateCallback
  onOpened/onDisconnected/onError/onClosed, createCaptureRequest(TEMPLATE_RECORD),
  createCaptureSession(SessionConfiguration), isSessionConfigurationSupported.
- params.SessionConfiguration(SESSION_REGULAR,List<OutputConfiguration>,Executor,
  CameraCaptureSession.StateCallback), OutputConfiguration(Surface). Если старый HAL
  не реализует query и бросает UnsupportedOperationException — обязательно ждём
  настоящие onConfigured, CaptureCallback.onCaptureCompleted и ключевой кадр.
- CameraCaptureSession.setRepeatingRequest/capture/close; CaptureRequest.Builder
  addTarget/set/build. Повторное открытие ждёт onClosed даже при остановке во время
  незавершённого openCamera. HandlerThread жив до последнего onClosed.
- Все используемые request keys и типы сверены в CaptureRequest.java:
  CONTROL_AE_TARGET_FPS_RANGE (Range<Integer>), CONTROL_AE_MODE/LOCK,
  CONTROL_AF_MODE/TRIGGER, LENS_FOCUS_DISTANCE, CONTROL_ZOOM_RATIO,
  SCALER_CROP_REGION, CONTROL_AE_EXPOSURE_COMPENSATION, CONTROL_AWB_MODE/LOCK,
  FLASH_MODE, CONTROL_VIDEO_STABILIZATION_MODE, LENS_OPTICAL_STABILIZATION_MODE,
  SENSOR_SENSITIVITY (Integer), SENSOR_EXPOSURE_TIME/FRAME_DURATION (Long).
  Доступность каждого проверяется через availableCaptureRequestKeys. Диапазоны —
  из CameraCharacteristics, включая fallback SCALER_AVAILABLE_MAX_DIGITAL_ZOOM и
  SENSOR_INFO_ACTIVE_ARRAY_SIZE для цифрового центрированного crop на API30.
- CameraMetadata.java: значения AF OFF/AUTO/MACRO/CONTINUOUS_VIDEO/CONTINUOUS_PICTURE/
  EDOF; AWB OFF/AUTO/INCANDESCENT/FLUORESCENT/WARM_FLUORESCENT/DAYLIGHT/
  CLOUDY_DAYLIGHT/TWILIGHT/SHADE; AE OFF/ON; FLASH OFF/TORCH; AF_TRIGGER_START;
  EIS/OIS OFF. Числа в русских подписях Windows взяты отсюда, не из догадок.
- CaptureResult.SENSOR_EXPOSURE_TIME/SENSOR_SENSITIVITY: фактические значения HAL;
  фактическая частота — число onCaptureCompleted / monotonic interval, не обещание
  точного FPS из диапазона. Метка видеопакета камеры — monotonic arrival кодированного
  кадра; не используется для заявления о сквозной задержке или camera sensor clock.
- MediaCodec/MediaCodecList/VideoCapabilities/MediaFormat/Surface — тот же проверенный
  в B путь; камера выдаёт Surface прямо кодировщику, без CPU ImageReader/YUV-копии.
  Аппаратный AVC/HEVC при наличии; обязательный AVC и software fallback (включая AVD).
- FGS camera type, permissions CAMERA/FOREGROUND_SERVICE_CAMERA, отдельный notification
  channel; служба запускается с видимого Activity после разрешения, START_NOT_STICKY.
  Не стартует из BOOT_COMPLETED, фоновой команды ПК или заблокированного Activity.
- PowerManager.PARTIAL_WAKE_LOCK, newWakeLock/acquire/release/isHeld:
  https://github.com/aosp-mirror/platform_frameworks_base/blob/android-16.0.0_r1/core/java/android/os/PowerManager.java
  WAKE_LOCK нужен только камере; освобождается при остановке/ошибке. Это не обход
  camera privacy/keyguard/OEM. Чёрный fallback — Activity с screenBrightness 0.01,
  FLAG_KEEP_SCREEN_ON, без снятия блокировки/администратора устройства:
  https://github.com/aosp-mirror/platform_frameworks_base/blob/android-16.0.0_r1/core/java/android/view/WindowManager.java
- Общий жизненный цикл следует официальному Camera2 controller sample:
  https://github.com/android/camera-samples/blob/main/core-camera/src/main/java/com/android/camera/core/camera2/BaseCamera2Controller.kt

Windows: стандартный WPF, никаких новых COM/native API в C. LayoutTransform,
TransformGroup.Children, ScaleTransform(double,double), RotateTransform(double),
ToggleButton.IsThreeState / nullable IsChecked сверены в официальном dotnet/wpf:
https://github.com/dotnet/wpf/tree/main/src/Microsoft.DotNet.Wpf/src
Поворот/отражение пока применяются к предпросмотру, не к Annex B. В блоке D эти
метаданные должны также применяться к NV12 перед virtual-camera output.

CI: отдельные эмулированные front/back камеры, настоящий Camera2 + MediaCodec;
KEYCODE_SLEEP=223/WAKEUP=224 сверены в SDK KeyEvent.java. Это не доказательство
работы ColorOS/MIUI, 10+ минут под keyguard или MF virtual camera.


## Диагностика C · порядок готовности (0.3.1)

Повторно сверены официальные CameraCaptureSession.CaptureCallback.onCaptureCompleted
и MediaCodec dequeueOutputBuffer/INFO_OUTPUT_FORMAT_CHANGED/BUFFER_FLAG_KEY_FRAME:
- https://github.com/aosp-mirror/platform_frameworks_base/blob/android-16.0.0_r1/core/java/android/hardware/camera2/CameraCaptureSession.java
- https://github.com/aosp-mirror/platform_frameworks_base/blob/android-16.0.0_r1/media/java/android/media/MediaCodec.java

Camera2 metadata callback не задаёт порядок относительно отдельного consumer
поверхности/MediaCodec output. Handler сериализует исполнение, но не гарантирует,
что capture callback окажется перед первым encoded IDR. Подтверждения нужно
запоминать независимо в текущем generation; требование нового CSD/IDR и capture
после reset сохраняется. Новых системных API для исправления не добавлено.
