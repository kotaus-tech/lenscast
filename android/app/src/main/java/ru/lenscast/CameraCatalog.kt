package ru.lenscast

import android.content.Context
import android.hardware.camera2.CameraCharacteristics as C
import android.hardware.camera2.CameraManager
import android.hardware.camera2.CaptureRequest
import android.media.MediaCodec
import android.util.Range
import android.util.Rational

/** Raw device declarations. NOT a promise of working encoder/session/FPS combinations. */
internal data class CameraStreamCandidate(val width: Int,val height: Int,val minFrameNanos: Long)
internal data class CameraControls(
 val aeModes: List<Int>,val afModes: List<Int>,val awbModes: List<Int>,
 val zoom: Range<Float>?,val minimumFocusDiopters: Float?,
 val compensation: Range<Int>?,val compensationStep: Rational?,
 val aeLock: Boolean,val awbLock: Boolean,val flash: Boolean,
 val videoStabilization: List<Int>,val opticalStabilization: List<Int>,
 val sensitivity: Range<Int>?,val exposureNanos: Range<Long>?,val maxFrameNanos: Long?
)
internal data class CameraDescriptor(
 val id: String,val facing: Int?,val orientation: Int?,val hardwareLevel: Int?,
 val physicalIds: Set<String>,val focalLengths: List<Float>,val capabilities: Set<Int>,
 val requestKeys: Set<CaptureRequest.Key<*>>,val streams: List<CameraStreamCandidate>,
 val aeFpsRanges: List<Range<Int>>,val controls: CameraControls
) {
 fun mayTryFps(stream: CameraStreamCandidate,fps: Int): Boolean =
  stream in streams && aeFpsRanges.any { CameraModePolicy.mayTryFps(fps,it.lower,it.upper,stream.minFrameNanos) }
 fun supports(key: CaptureRequest.Key<*>): Boolean = key in requestKeys
}

/**
 * First component of C. Read only after entering camera mode; never called by screen capture.
 * Enumerate directly openable IDs only. Physical-only IDs stay metadata, not fake lens buttons.
 * No openCamera, permission request, persistence, assumed modes or camera/microphone activation.
 */
internal object CameraCatalog {
 fun discover(context: Context): List<CameraDescriptor> {
  val manager=context.getSystemService(CameraManager::class.java) ?: return emptyList()
  val ids=try { manager.cameraIdList } catch(e: Exception) {
   State.log(context,"Не удалось прочитать список камер. Откройте режим камеры ещё раз.",e)
   return emptyList()
  }
  return ids.mapNotNull { id ->
   try { read(id,manager.getCameraCharacteristics(id)) } catch(e: Exception) {
    State.log(context,"Не удалось прочитать возможности камеры. Выберите другую камеру.",e)
    null
   }
  }
 }
 private fun read(id: String,c: C): CameraDescriptor {
  val map=c.get(C.SCALER_STREAM_CONFIGURATION_MAP)
  val streams=map?.let { config ->
   config.getOutputSizes(MediaCodec::class.java).orEmpty().map { size ->
    CameraStreamCandidate(size.width,size.height,config.getOutputMinFrameDuration(MediaCodec::class.java,size))
   }
  }.orEmpty().filter { it.width>0 && it.height>0 && it.minFrameNanos>=0 }
   .distinctBy { Pair(it.width,it.height) }.sortedByDescending { it.width.toLong()*it.height }
  return CameraDescriptor(
   id=id,facing=c.get(C.LENS_FACING),orientation=c.get(C.SENSOR_ORIENTATION),
   hardwareLevel=c.get(C.INFO_SUPPORTED_HARDWARE_LEVEL),physicalIds=c.physicalCameraIds.toSet(),
   focalLengths=c.get(C.LENS_INFO_AVAILABLE_FOCAL_LENGTHS)?.toList().orEmpty(),
   capabilities=c.get(C.REQUEST_AVAILABLE_CAPABILITIES)?.toSet().orEmpty(),
   requestKeys=c.availableCaptureRequestKeys.toSet(),streams=streams,
   aeFpsRanges=c.get(C.CONTROL_AE_AVAILABLE_TARGET_FPS_RANGES)?.toList().orEmpty(),
   controls=CameraControls(
    aeModes=c.get(C.CONTROL_AE_AVAILABLE_MODES)?.toList().orEmpty(),
    afModes=c.get(C.CONTROL_AF_AVAILABLE_MODES)?.toList().orEmpty(),
    awbModes=c.get(C.CONTROL_AWB_AVAILABLE_MODES)?.toList().orEmpty(),
    zoom=c.get(C.CONTROL_ZOOM_RATIO_RANGE),minimumFocusDiopters=c.get(C.LENS_INFO_MINIMUM_FOCUS_DISTANCE),
    compensation=c.get(C.CONTROL_AE_COMPENSATION_RANGE),compensationStep=c.get(C.CONTROL_AE_COMPENSATION_STEP),
    aeLock=c.get(C.CONTROL_AE_LOCK_AVAILABLE)==true,awbLock=c.get(C.CONTROL_AWB_LOCK_AVAILABLE)==true,
    flash=c.get(C.FLASH_INFO_AVAILABLE)==true,
    videoStabilization=c.get(C.CONTROL_AVAILABLE_VIDEO_STABILIZATION_MODES)?.toList().orEmpty(),
    opticalStabilization=c.get(C.LENS_INFO_AVAILABLE_OPTICAL_STABILIZATION)?.toList().orEmpty(),
    sensitivity=c.get(C.SENSOR_INFO_SENSITIVITY_RANGE),exposureNanos=c.get(C.SENSOR_INFO_EXPOSURE_TIME_RANGE),
    maxFrameNanos=c.get(C.SENSOR_INFO_MAX_FRAME_DURATION)
   )
  )
 }
}
