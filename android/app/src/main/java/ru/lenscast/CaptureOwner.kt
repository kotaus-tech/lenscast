package ru.lenscast

/** A single video producer, including the asynchronous service teardown interval. */
internal object CaptureOwner {
 private var owner=""
 @Synchronized fun claim(value: String): Boolean { if(owner.isNotEmpty()) return false; owner=value; return true }
 @Synchronized fun release(value: String) { if(owner==value) owner="" }
}
