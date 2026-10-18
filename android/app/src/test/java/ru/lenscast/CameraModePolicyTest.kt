package ru.lenscast

import org.junit.Assert.*
import org.junit.Test

class CameraModePolicyTest {
 @Test fun usesDeclaredAeRangeRatherThanAssumingThirtyOrSixty() {
  assertTrue(CameraModePolicy.mayTryFps(24,15,24,40_000_000))
  assertFalse(CameraModePolicy.mayTryFps(30,15,24,0))
  assertFalse(CameraModePolicy.mayTryFps(60,15,30,0))
 }
 @Test fun rejectsSizeWhichCannotReachRequestedFps() {
  assertTrue(CameraModePolicy.mayTryFps(30,15,60,33_333_334))
  assertFalse(CameraModePolicy.mayTryFps(30,15,60,33_333_335))
  assertFalse(CameraModePolicy.mayTryFps(60,15,60,33_333_334))
 }
 @Test fun unknownDurationCanOnlyBeProbedNotTreatedAsConfirmedSupport() {
  assertTrue(CameraModePolicy.mayTryFps(30,15,30,0))
  assertFalse(CameraModePolicy.mayTryFps(0,15,30,0))
  assertFalse(CameraModePolicy.mayTryFps(-1,15,30,0))
  assertFalse(CameraModePolicy.mayTryFps(30,30,15,0))
  assertFalse(CameraModePolicy.mayTryFps(30,0,30,0))
  assertFalse(CameraModePolicy.mayTryFps(30,15,30,-1))
 }
}
