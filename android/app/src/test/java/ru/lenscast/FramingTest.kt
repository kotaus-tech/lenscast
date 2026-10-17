package ru.lenscast

import java.io.*
import java.net.SocketTimeoutException
import org.junit.Assert.*
import org.junit.Test

class FramingTest {
 private fun packet(size: Int) = ByteArrayOutputStream().also { DataOutputStream(it).writeInt(size) }.toByteArray()
 @Test fun roundTrip() {
  val output = ByteArrayOutputStream()
  val expected = "{\"type\":\"ping\"}".toByteArray()
  Framing.write(DataOutputStream(output), expected)
  assertArrayEquals(expected, Framing.read(DataInputStream(ByteArrayInputStream(output.toByteArray()))))
 }
 @Test fun fragmentedStream() {
  val output = ByteArrayOutputStream()
  Framing.write(DataOutputStream(output), "{}".toByteArray())
  val input = object : ByteArrayInputStream(output.toByteArray()) {
   override fun read(b: ByteArray, off: Int, len: Int): Int = super.read(b, off, minOf(1, len))
   override fun available(): Int = 0 // SSL available() is NOT a readiness signal.
  }
  assertEquals("{}", String(Framing.read(DataInputStream(input))!!))
 }
 @Test fun idleTimeoutIsAllowed() {
  val input = object : InputStream() { override fun read(): Int = throw SocketTimeoutException() }
  assertNull(Framing.read(DataInputStream(input)))
 }
 @Test(expected = SocketTimeoutException::class) fun partialTimeoutIsFatal() {
  var count = 0
  val input = object : InputStream() { override fun read(): Int = if (count++ == 0) 0 else throw SocketTimeoutException() }
  Framing.read(DataInputStream(input))
 }
 @Test(expected = EOFException::class) fun partialBodyIsFatal() {
  Framing.read(DataInputStream(ByteArrayInputStream(packet(2) + byteArrayOf(123))))
 }
 @Test fun invalidLengths() {
  for (n in listOf(-1, 0, 1, 65537, Int.MAX_VALUE)) {
   try { Framing.read(DataInputStream(ByteArrayInputStream(packet(n)))); fail("Длина принята: $n") }
   catch (_: IllegalArgumentException) {}
  }
 }
}
