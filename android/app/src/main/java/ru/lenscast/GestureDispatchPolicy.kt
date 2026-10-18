package ru.lenscast

/** A continued stationary DOWN has no Android MotionEvent; UP must never be skipped. */
object GestureDispatchPolicy {
 fun needsSegment(hasStroke: Boolean, down: Boolean, x: Float,y: Float,endX: Float,endY: Float): Boolean =
  !hasStroke || !down || x!=endX || y!=endY
}
