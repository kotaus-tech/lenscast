package ru.lenscast

import org.junit.Assert.*
import org.junit.Test

class CameraReadinessTest {
 @Test fun allCallbackOrdersRequireAllThreeProofs() {
  for(order in listOf("CKI","CIK","KCI","KIC","ICK","IKC")) {
   val proof=CameraReadiness(); proof.reset(1)
   order.forEachIndexed { index,event ->
    when(event) { 'C'->proof.capture(1); 'K'->proof.config(1); 'I'->proof.key(1) }
    assertEquals(order,index==2,proof.ready)
   }
  }
 }
 @Test fun firstIdrBeforeCaptureDoesNotRequireASecondIdr() {
  val proof=CameraReadiness(); proof.reset(1)
  proof.config(1); proof.key(1); assertFalse(proof.ready)
  proof.capture(1); assertTrue(proof.ready)
 }
 @Test fun resetAndStaleCallbacksCannotReusePreviousStreamProofs() {
  val proof=CameraReadiness(); proof.reset(1)
  proof.capture(1); proof.config(1); proof.key(1); assertTrue(proof.ready)
  repeat(10) { i ->
   val epoch=i+2; proof.reset(epoch); assertFalse(proof.ready)
   proof.capture(epoch-1); proof.config(epoch-1); proof.key(epoch-1); assertFalse(proof.ready)
   proof.capture(epoch); proof.config(epoch); assertFalse(proof.ready)
   proof.key(epoch); assertTrue(proof.ready)
  }
 }
}
