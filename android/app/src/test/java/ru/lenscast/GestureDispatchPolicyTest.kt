package ru.lenscast

import org.junit.Assert.*
import org.junit.Test

class GestureDispatchPolicyTest {
 @Test fun downAndUpAreAlwaysDispatchedButStationaryContinuationIsNot() {
  assertTrue(GestureDispatchPolicy.needsSegment(false,true,20f,30f,20f,30f))
  repeat(100) { assertFalse(GestureDispatchPolicy.needsSegment(true,true,20f,30f,20f,30f)) }
  assertTrue(GestureDispatchPolicy.needsSegment(true,false,20f,30f,20f,30f))
 }
 @Test fun liveDragStillDispatchesMovement() {
  assertTrue(GestureDispatchPolicy.needsSegment(true,true,20f,30f,21f,30f))
  assertTrue(GestureDispatchPolicy.needsSegment(true,true,20f,30f,20f,31f))
 }
}
