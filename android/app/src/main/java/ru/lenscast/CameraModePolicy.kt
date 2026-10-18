package ru.lenscast

/** Candidate screening only: neither metadata nor this calculation proves a working session. */
internal object CameraModePolicy {
 fun mayTryFps(fps: Int, aeLower: Int, aeUpper: Int, minFrameNanos: Long): Boolean {
  if(fps<=0 || aeLower<=0 || aeUpper<aeLower || fps !in aeLower..aeUpper || minFrameNanos<0) return false
  // HAL durations are integer nanoseconds; allow ceil(1 second / fps).
  // A zero duration means UNKNOWN, not unlimited or tested FPS.
  return minFrameNanos==0L || minFrameNanos<=(1_000_000_000L+fps-1)/fps
 }
}
