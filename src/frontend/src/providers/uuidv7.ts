// RFC9562 §5.7. Checked against the independent Appendix A.6 vector.
// No monotonic ordering guarantee within a millisecond is needed for admission.
export function uuidv7(now = Date.now()): string {
  if (!Number.isSafeInteger(now) || now < 0 || now > 0xffffffffffff) throw new Error('Invalid UUID timestamp')
  const bytes = crypto.getRandomValues(new Uint8Array(16))
  let time = now
  for (let index = 5; index >= 0; index--) {
    bytes[index] = time % 256
    time = Math.floor(time / 256)
  }
  bytes[6] = (bytes[6] & 0x0f) | 0x70
  bytes[8] = (bytes[8] & 0x3f) | 0x80
  const hex = Array.from(bytes, (byte) => byte.toString(16).padStart(2, '0')).join('')
  return `${hex.slice(0,8)}-${hex.slice(8,12)}-${hex.slice(12,16)}-${hex.slice(16,20)}-${hex.slice(20)}`
}
