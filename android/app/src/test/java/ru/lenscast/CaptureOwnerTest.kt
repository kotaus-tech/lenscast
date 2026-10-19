package ru.lenscast

import org.junit.Assert.*
import org.junit.Test

class CaptureOwnerTest {
 @Test fun producersCannotOverlapEvenDuringAsynchronousTeardown() {
  try {
   assertTrue(CaptureOwner.claim("screen"))
   assertFalse(CaptureOwner.claim("camera"))
   assertFalse(CaptureOwner.claim("screen"))
   CaptureOwner.release("camera")
   assertFalse(CaptureOwner.claim("camera"))
   CaptureOwner.release("screen")
   assertTrue(CaptureOwner.claim("camera"))
   assertFalse(CaptureOwner.claim("screen"))
  } finally { CaptureOwner.release("screen"); CaptureOwner.release("camera") }
 }
}
