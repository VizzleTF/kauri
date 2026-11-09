//
//  KauriCurve.swift
//
//  The edwards25519 group operations SPAKE2 needs (docs/sync.md §8.1), ported
//  from TweetNaCl (public domain): field elements are 16 limbs of 16 bits,
//  points use extended coordinates, and the scalar multiplication swaps in
//  constant time. Only pairing uses this, so it favours size over speed.
//
//  The curve constants are derived at first use rather than written out.
//

// swiftlint:disable identifier_name
import Foundation

enum Ed25519Group {
    typealias Field = [Int64] // 16 limbs
    typealias Point = [Field] // X, Y, Z, T

    // MARK: - Field arithmetic mod 2^255 - 19

    private static let zero: Field = Array(repeating: 0, count: 16)
    private static let one: Field = [1] + Array(repeating: 0, count: 15)

    private static func car(_ o: inout Field) {
        for i in 0..<16 {
            o[i] &+= 1 << 16
            let c = o[i] >> 16
            if i < 15 {
                o[i + 1] &+= c &- 1
            } else {
                o[0] &+= 38 &* (c &- 1)
            }
            o[i] &-= c << 16
        }
    }

    /// Swaps p and q if b is 1, in constant time.
    private static func sel(_ p: inout Field, _ q: inout Field, _ b: Int64) {
        let c = ~(b &- 1)
        for i in 0..<16 {
            let t = c & (p[i] ^ q[i])
            p[i] ^= t
            q[i] ^= t
        }
    }

    private static func pack(_ n: Field) -> [UInt8] {
        var t = n
        car(&t)
        car(&t)
        car(&t)
        var m = zero
        for _ in 0..<2 {
            m[0] = t[0] &- 0xffed
            for i in 1..<15 {
                m[i] = t[i] &- 0xffff &- ((m[i - 1] >> 16) & 1)
                m[i - 1] &= 0xffff
            }
            m[15] = t[15] &- 0x7fff &- ((m[14] >> 16) & 1)
            let b = (m[15] >> 16) & 1
            m[14] &= 0xffff
            sel(&t, &m, 1 &- b)
        }
        var o = [UInt8](repeating: 0, count: 32)
        for i in 0..<16 {
            o[2 * i] = UInt8(t[i] & 0xff)
            o[2 * i + 1] = UInt8((t[i] >> 8) & 0xff)
        }
        return o
    }

    private static func unpack(_ n: [UInt8]) -> Field {
        var o = zero
        for i in 0..<16 { o[i] = Int64(n[2 * i]) + (Int64(n[2 * i + 1]) << 8) }
        o[15] &= 0x7fff
        return o
    }

    private static func equal(_ a: Field, _ b: Field) -> Bool {
        zip(pack(a), pack(b)).reduce(UInt8(0)) { $0 | ($1.0 ^ $1.1) } == 0
    }

    private static func parity(_ a: Field) -> UInt8 { pack(a)[0] & 1 }

    private static func add(_ a: Field, _ b: Field) -> Field { (0..<16).map { a[$0] &+ b[$0] } }
    private static func sub(_ a: Field, _ b: Field) -> Field { (0..<16).map { a[$0] &- b[$0] } }

    private static func mul(_ a: Field, _ b: Field) -> Field {
        var t = [Int64](repeating: 0, count: 31)
        for i in 0..<16 {
            for j in 0..<16 { t[i + j] &+= a[i] &* b[j] }
        }
        for i in 0..<15 { t[i] &+= 38 &* t[i + 16] }
        var o = Array(t[0..<16])
        car(&o)
        car(&o)
        return o
    }

    private static func square(_ a: Field) -> Field { mul(a, a) }

    private static func inverse(_ i: Field) -> Field {
        var c = i
        for a in stride(from: 253, through: 0, by: -1) {
            c = square(c)
            if a != 2 && a != 4 { c = mul(c, i) }
        }
        return c
    }

    private static func pow2523(_ i: Field) -> Field {
        var c = i
        for a in stride(from: 250, through: 0, by: -1) {
            c = square(c)
            if a != 1 { c = mul(c, i) }
        }
        return c
    }

    /// base^exponent, exponent little-endian. Not constant time: constants only.
    private static func pow(_ base: Field, _ exponent: [UInt8]) -> Field {
        var r = one
        for bit in stride(from: 255, through: 0, by: -1) {
            r = square(r)
            if (exponent[bit / 8] >> (bit % 8)) & 1 == 1 { r = mul(r, base) }
        }
        return r
    }

    private static func small(_ n: Int64) -> Field { [n & 0xffff, n >> 16] + Array(repeating: 0, count: 14) }

    // d = -121665/121666, sqrt(-1) = 2^((p-1)/4), and the base point.
    private static let d: Field = mul(sub(zero, small(121_665)), inverse(small(121_666)))
    private static let d2: Field = add(d, d)
    private static let sqrtM1: Field = pow(small(2), [0xfb] + Array(repeating: 0xff, count: 30) + [0x1f])
    private static let base: Point = decode([0x58] + Array(repeating: 0x66, count: 31))!
    private static let identityBytes: [UInt8] = [1] + Array(repeating: 0, count: 31)

    // MARK: - Points

    private static func add(_ p: Point, _ q: Point) -> Point {
        let a = mul(sub(p[1], p[0]), sub(q[1], q[0]))
        let b = mul(add(p[0], p[1]), add(q[0], q[1]))
        let c = mul(mul(p[3], q[3]), d2)
        var dd = mul(p[2], q[2])
        dd = add(dd, dd)
        let e = sub(b, a), f = sub(dd, c), g = add(dd, c), h = add(b, a)
        return [mul(e, f), mul(h, g), mul(g, f), mul(e, h)]
    }

    private static func negate(_ p: Point) -> Point {
        [sub(zero, p[0]), p[1], p[2], sub(zero, p[3])]
    }

    private static func encode(_ p: Point) -> [UInt8] {
        let zi = inverse(p[2])
        var r = pack(mul(p[1], zi))
        r[31] ^= parity(mul(p[0], zi)) << 7
        return r
    }

    /// Decodes a canonical encoding of a point on the curve.
    private static func decode(_ s: [UInt8]) -> Point? {
        guard s.count == 32 else { return nil }
        let y = unpack(s)
        var canonical = s
        canonical[31] &= 0x7f
        guard pack(y) == canonical else { return nil } // y >= p
        let z = one
        var num = square(y)
        var den = mul(num, d)
        num = sub(num, z)
        den = add(z, den)
        let den2 = square(den), den4 = square(den2), den6 = mul(den4, den2)
        var t = mul(mul(den6, num), den)
        t = pow2523(t)
        t = mul(mul(mul(t, num), den), den)
        var x = mul(t, den)
        if !equal(mul(square(x), den), num) { x = mul(x, sqrtM1) }
        guard equal(mul(square(x), den), num) else { return nil }
        let sign = s[31] >> 7
        if equal(x, zero) && sign == 1 { return nil }
        if parity(x) != sign { x = sub(zero, x) }
        return [x, y, z, mul(x, y)]
    }

    private static func scalarMult(_ q: Point, _ s: [UInt8]) -> Point {
        var p: Point = [zero, one, one, zero]
        var q = q
        for i in stride(from: 255, through: 0, by: -1) {
            let b = Int64((s[i / 8] >> (i & 7)) & 1)
            for k in 0..<4 { sel(&p[k], &q[k], b) }
            q = add(q, p)
            p = add(p, p)
            for k in 0..<4 { sel(&p[k], &q[k], b) }
        }
        return p
    }

    // MARK: - Scalars mod L

    private static let l: [Int64] = [
        0xed, 0xd3, 0xf5, 0x5c, 0x1a, 0x63, 0x12, 0x58, 0xd6, 0x9c, 0xf7, 0xa2, 0xde, 0xf9, 0xde, 0x14,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0x10
    ]

    private static func modL(_ x: inout [Int64]) -> [UInt8] {
        for i in stride(from: 63, through: 32, by: -1) {
            var carry: Int64 = 0
            var j = i - 32
            while j < i - 12 {
                x[j] &+= carry &- 16 &* x[i] &* l[j - (i - 32)]
                carry = (x[j] &+ 128) >> 8
                x[j] &-= carry << 8
                j += 1
            }
            x[j] &+= carry
            x[i] = 0
        }
        var carry: Int64 = 0
        for j in 0..<32 {
            x[j] &+= carry &- (x[31] >> 4) &* l[j]
            carry = x[j] >> 8
            x[j] &= 255
        }
        for j in 0..<32 { x[j] &-= carry &* l[j] }
        var r = [UInt8](repeating: 0, count: 32)
        for i in 0..<32 {
            x[i + 1] &+= x[i] >> 8
            r[i] = UInt8(x[i] & 255)
        }
        return r
    }

    // MARK: - Public operations (the libsodium names)

    /// crypto_core_ed25519_scalar_reduce: a 64-byte number mod L.
    static func scalarReduce(_ s: Data) -> Data {
        var x = [Int64](repeating: 0, count: 64)
        for (i, b) in s.prefix(64).enumerated() { x[i] = Int64(b) }
        return Data(modL(&x))
    }

    /// crypto_core_ed25519_scalar_mul: a·b mod L.
    static func scalarMul(_ a: Data, _ b: Data) -> Data {
        let a = [UInt8](a), b = [UInt8](b)
        var x = [Int64](repeating: 0, count: 64)
        for i in 0..<32 {
            for j in 0..<32 { x[i + j] &+= Int64(a[i]) &* Int64(b[j]) }
        }
        return Data(modL(&x))
    }

    /// crypto_scalarmult_ed25519_base_noclamp.
    static func baseMult(_ n: Data) -> Data {
        var s = [UInt8](n)
        s[31] &= 0x7f
        return Data(encode(scalarMult(base, s)))
    }

    /// crypto_scalarmult_ed25519_noclamp: nil for an invalid or small-order
    /// point, or if the result is the identity.
    static func mult(_ n: Data, _ p: Data) -> Data? {
        guard let q = decode([UInt8](p)), !hasSmallOrder(q) else { return nil }
        var s = [UInt8](n)
        s[31] &= 0x7f
        let r = encode(scalarMult(q, s))
        return r == identityBytes ? nil : Data(r)
    }

    /// crypto_core_ed25519_add / _sub: nil if either point does not decode.
    static func add(_ p: Data, _ q: Data) -> Data? {
        guard let a = decode([UInt8](p)), let b = decode([UInt8](q)) else { return nil }
        return Data(encode(add(a, b)))
    }

    static func sub(_ p: Data, _ q: Data) -> Data? {
        guard let a = decode([UInt8](p)), let b = decode([UInt8](q)) else { return nil }
        return Data(encode(add(a, negate(b))))
    }

    /// crypto_core_ed25519_is_valid_point: canonical, on the curve, not of
    /// small order and in the prime-order subgroup.
    static func isValidPoint(_ p: Data) -> Bool {
        guard let q = decode([UInt8](p)), !hasSmallOrder(q) else { return false }
        return encode(scalarMult(q, l.map { UInt8($0) })) == identityBytes
    }

    private static func hasSmallOrder(_ q: Point) -> Bool {
        var p = q
        for _ in 0..<3 { p = add(p, p) }
        return encode(p) == identityBytes
    }
}

// swiftlint:enable identifier_name
