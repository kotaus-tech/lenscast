package ru.lenscast

/** Independent Camera2/MediaCodec callbacks may arrive in either order. Worker-confined. */
internal class CameraReadiness {
 private var generation=-1
 var captured=false; private set
 var configured=false; private set
 var keyFrame=false; private set
 val ready get()=captured && configured && keyFrame
 fun reset(epoch: Int) { generation=epoch; captured=false; configured=false; keyFrame=false }
 fun capture(epoch: Int) { if(epoch==generation) captured=true }
 fun config(epoch: Int) { if(epoch==generation) configured=true }
 fun key(epoch: Int) { if(epoch==generation) keyFrame=true }
}
