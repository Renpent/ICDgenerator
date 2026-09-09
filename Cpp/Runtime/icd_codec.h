// icd_codec.h — runtime support for the generated ICD codecs.
//
// Hand-written and copied out verbatim by the generator; nothing here is derived from a FOM.
// C++11, no dependencies beyond the standard library, no platform headers.
//
// Every record is a fixed-size box. A dynamic array occupies its ceiling whatever it actually
// carries — a count, then room for the agreed maximum, the tail zero-filled — so a class always
// encodes to the same number of bytes. That is what makes encodedSize a constant, lets a datagram
// state "N records of M bytes" once in its header instead of a length in front of every record, and
// puts record k at a computable offset. It costs the unused tail of every array, which is the trade
// the ceilings exist to make.
//
// Byte order is big-endian, fixed. That is a property of the datagram the ICD specifies, not of the
// machine, so it is not a build option: reading a byte at a time makes it independent of the host's
// own byte order and avoids unaligned access, and compilers fold the pattern back into a single
// load plus a bswap. If a layout ever needs little-endian, change the six load/store functions —
// nothing else in the generated tree encodes an order — and do not add an #ifdef.
//
// Identifiers here deliberately avoid the prefix that generated type names can carry. That prefix
// exists so a project can search-and-replace it and point these codecs at its own HLA-generated
// type definitions; an include guard or macro caught by that replace would be a puzzling breakage,
// so the token appears nowhere in this file.

#ifndef ICDCODEC_H
#define ICDCODEC_H

#include <stdint.h>
#include <stddef.h>
#include <string.h>

#include <array>
#include <vector>

namespace icd {

enum class Result {
    Ok = 0,
    Truncated,      ///< ran past the end of the buffer
    CorruptCount,   ///< an element count above the array's agreed ceiling
    ShortBuffer,    ///< the destination buffer cannot hold the encoding
    TooManyItems,   ///< a container holds more elements than its ceiling allows
    BadMagic,       ///< not an ICD datagram, or the sender's byte order differs
    WrongClass,     ///< the datagram carries a different class than expected
    RecordTooSmall  ///< the sender's records are shorter than this build expects
};

/// Width of the element count that precedes a variable-length array. A datagram caps the count long
/// before 16 bits overflow, and 8 bits does not survive an array of single-byte elements.
typedef uint16_t Count;
enum : size_t { kCountSize = 2 };

/// HLAboolean. The standard MIM encodes it over HLAinteger32BE, but that is HLA's encoding and this
/// datagram is not HLA's; a boolean here is one byte. A FOM declaring a boolean of its own keeps
/// whatever width it states.
enum : size_t { kBooleanSize = 1 };

// ---------------------------------------------------------------------------
// Raw loads and stores
//
// The six multi-byte functions below are the only place an order is decided. Nothing in the
// generated tree spells one out, so swapping the datagram to little-endian means editing these and
// nothing else.
// ---------------------------------------------------------------------------

inline uint8_t loadU8(const unsigned char* p) { return p[0]; }

inline uint16_t loadU16(const unsigned char* p) {
    return static_cast<uint16_t>((static_cast<unsigned>(p[0]) << 8) |
                                 static_cast<unsigned>(p[1]));
}

inline uint32_t loadU32(const unsigned char* p) {
    return (static_cast<uint32_t>(p[0]) << 24) | (static_cast<uint32_t>(p[1]) << 16) |
           (static_cast<uint32_t>(p[2]) << 8) | static_cast<uint32_t>(p[3]);
}

inline uint64_t loadU64(const unsigned char* p) {
    return (static_cast<uint64_t>(loadU32(p)) << 32) | static_cast<uint64_t>(loadU32(p + 4));
}

inline void storeU8(unsigned char* p, uint8_t v) { p[0] = v; }

inline void storeU16(unsigned char* p, uint16_t v) {
    p[0] = static_cast<unsigned char>((v >> 8) & 0xFFu);
    p[1] = static_cast<unsigned char>(v & 0xFFu);
}

inline void storeU32(unsigned char* p, uint32_t v) {
    p[0] = static_cast<unsigned char>((v >> 24) & 0xFFu);
    p[1] = static_cast<unsigned char>((v >> 16) & 0xFFu);
    p[2] = static_cast<unsigned char>((v >> 8) & 0xFFu);
    p[3] = static_cast<unsigned char>(v & 0xFFu);
}

inline void storeU64(unsigned char* p, uint64_t v) {
    storeU32(p, static_cast<uint32_t>((v >> 32) & 0xFFFFFFFFu));
    storeU32(p + 4, static_cast<uint32_t>(v & 0xFFFFFFFFu));
}

// Floating point goes through memcpy. A union or a reinterpret_cast violates strict aliasing and
// does break under -O2; memcpy is the only conforming spelling and compilers reduce it to a move.
inline float loadF32(const unsigned char* p) {
    uint32_t bits = loadU32(p);
    float value;
    memcpy(&value, &bits, sizeof value);
    return value;
}

inline double loadF64(const unsigned char* p) {
    uint64_t bits = loadU64(p);
    double value;
    memcpy(&value, &bits, sizeof value);
    return value;
}

inline void storeF32(unsigned char* p, float value) {
    uint32_t bits;
    memcpy(&bits, &value, sizeof bits);
    storeU32(p, bits);
}

inline void storeF64(unsigned char* p, double value) {
    uint64_t bits;
    memcpy(&bits, &value, sizeof bits);
    storeU64(p, bits);
}

// ---------------------------------------------------------------------------
// Bounds-checked cursors
// ---------------------------------------------------------------------------

/// Walks a buffer, refusing to hand out more than is left. Every read goes through take(), so a
/// truncated or hostile datagram cannot walk off the end.
class Reader {
public:
    Reader(const unsigned char* buf, size_t len) : at_(buf), end_(buf + len) {}

    size_t remaining() const { return static_cast<size_t>(end_ - at_); }

    /// Returns null rather than a short block when the bytes are not there.
    const unsigned char* take(size_t n) {
        if (remaining() < n) return 0;
        const unsigned char* start = at_;
        at_ += n;
        return start;
    }

    /// Steps over bytes without reading them — an array's unused tail, or a field this build does
    /// not know about because the sender was generated from a later ICD.
    bool skip(size_t n) { return take(n) != 0; }

private:
    const unsigned char* at_;
    const unsigned char* end_;
};

/// The writing counterpart. A failed write is latched rather than reported at every call site, so
/// generated encoders stay free of error checks; ok() is consulted once at the end.
class Writer {
public:
    Writer(unsigned char* buf, size_t cap) : begin_(buf), at_(buf), end_(buf + cap), ok_(true) {}

    unsigned char* take(size_t n) {
        if (static_cast<size_t>(end_ - at_) < n) {
            ok_ = false;
            return 0;
        }
        unsigned char* start = at_;
        at_ += n;
        return start;
    }

    /// Fills an array's unused tail. Zeros rather than whatever the buffer held, so two encodings of
    /// the same value are byte-identical and two captures can be diffed.
    void pad(size_t n) {
        unsigned char* p = take(n);
        if (p) memset(p, 0, n);
    }

    void fail() { ok_ = false; }
    bool ok() const { return ok_; }
    size_t written() const { return static_cast<size_t>(at_ - begin_); }

private:
    unsigned char* begin_;
    unsigned char* at_;
    unsigned char* end_;
    bool ok_;
};

// ---------------------------------------------------------------------------
// Encoded size
//
// Every type occupies a constant number of bytes, arrays included — they are written at their
// ceiling. Generated records carry kEncodedSize and the primary template picks it up.
// ---------------------------------------------------------------------------

template <class T>
struct FixedSize {
    enum : size_t { value = T::kEncodedSize };
};

template <> struct FixedSize<uint8_t>  { enum : size_t { value = 1 }; };
template <> struct FixedSize<uint16_t> { enum : size_t { value = 2 }; };
template <> struct FixedSize<uint32_t> { enum : size_t { value = 4 }; };
template <> struct FixedSize<uint64_t> { enum : size_t { value = 8 }; };
template <> struct FixedSize<int8_t>   { enum : size_t { value = 1 }; };
template <> struct FixedSize<int16_t>  { enum : size_t { value = 2 }; };
template <> struct FixedSize<int32_t>  { enum : size_t { value = 4 }; };
template <> struct FixedSize<int64_t>  { enum : size_t { value = 8 }; };
template <> struct FixedSize<float>    { enum : size_t { value = 4 }; };
template <> struct FixedSize<double>   { enum : size_t { value = 8 }; };
template <> struct FixedSize<bool>     { enum : size_t { value = kBooleanSize }; };

template <class T, size_t N>
struct FixedSize<std::array<T, N> > {
    enum : size_t { value = N * FixedSize<T>::value };
};

// ---------------------------------------------------------------------------
// Primitives
// ---------------------------------------------------------------------------

#define ICDCODEC_PRIMITIVE(TYPE, WIDTH, LOAD, STORE)                  \
    inline Result decode(Reader& r, TYPE& v) {                        \
        const unsigned char* p = r.take(WIDTH);                       \
        if (!p) return Result::Truncated;                             \
        v = static_cast<TYPE>(LOAD(p));                               \
        return Result::Ok;                                            \
    }                                                                 \
    inline void encode(Writer& w, TYPE v) {                           \
        unsigned char* p = w.take(WIDTH);                             \
        if (p) STORE(p, v);                                           \
    }                                                                 \
    inline size_t encodedSize(TYPE) { return WIDTH; }

ICDCODEC_PRIMITIVE(uint8_t, 1, loadU8, storeU8)
ICDCODEC_PRIMITIVE(uint16_t, 2, loadU16, storeU16)
ICDCODEC_PRIMITIVE(uint32_t, 4, loadU32, storeU32)
ICDCODEC_PRIMITIVE(uint64_t, 8, loadU64, storeU64)
ICDCODEC_PRIMITIVE(int8_t, 1, loadU8, storeU8)
ICDCODEC_PRIMITIVE(int16_t, 2, loadU16, storeU16)
ICDCODEC_PRIMITIVE(int32_t, 4, loadU32, storeU32)
ICDCODEC_PRIMITIVE(int64_t, 8, loadU64, storeU64)
ICDCODEC_PRIMITIVE(float, 4, loadF32, storeF32)
ICDCODEC_PRIMITIVE(double, 8, loadF64, storeF64)

#undef ICDCODEC_PRIMITIVE

// Any non-zero encoding reads as true. The wire carries a width, not a C++ bool, and a sender that
// writes something other than 0 or 1 has still said "true".
inline Result decode(Reader& r, bool& v) {
    uint8_t raw = 0;
    Result rc = decode(r, raw);
    if (rc != Result::Ok) return rc;
    v = raw != 0;
    return Result::Ok;
}

inline void encode(Writer& w, bool v) { encode(w, static_cast<uint8_t>(v ? 1 : 0)); }

inline size_t encodedSize(bool) { return kBooleanSize; }

// ---------------------------------------------------------------------------
// Fixed-length arrays
// ---------------------------------------------------------------------------

template <class T, size_t N>
Result decode(Reader& r, std::array<T, N>& v) {
    for (size_t i = 0; i < N; ++i) {
        Result rc = decode(r, v[i]);
        if (rc != Result::Ok) return rc;
    }
    return Result::Ok;
}

template <class T, size_t N>
void encode(Writer& w, const std::array<T, N>& v) {
    for (size_t i = 0; i < N; ++i) encode(w, v[i]);
}

template <class T, size_t N>
size_t encodedSize(const std::array<T, N>&) { return FixedSize<std::array<T, N> >::value; }

// ---------------------------------------------------------------------------
// Variable-length arrays
//
// A count, then room for `ceiling` elements with the tail zero-filled, so the field is the same
// size every time. The ceiling is an argument rather than part of the type: it is a federation
// agreement attached to the field, and two fields of the same element type may be given different
// ones. The generator passes the value it read from the array limits.
// ---------------------------------------------------------------------------

/// Bytes a bounded array occupies, whatever it currently holds.
template <class T>
size_t boundedSize(Count ceiling) {
    return kCountSize + static_cast<size_t>(ceiling) * FixedSize<T>::value;
}

template <class T>
Result decodeBounded(Reader& r, std::vector<T>& v, Count ceiling) {
    Count n = 0;
    Result rc = decode(r, n);
    if (rc != Result::Ok) return rc;

    // The ceiling is the authority now, not the bytes left: the field is that long by construction,
    // so a larger count is corruption rather than a longer record.
    if (n > ceiling) return Result::CorruptCount;

    v.clear();
    v.resize(n);
    for (Count i = 0; i < n; ++i) {
        rc = decode(r, v[i]);
        if (rc != Result::Ok) return rc;
    }

    return r.skip(static_cast<size_t>(ceiling - n) * FixedSize<T>::value)
        ? Result::Ok
        : Result::Truncated;
}

template <class T>
void encodeBounded(Writer& w, const std::vector<T>& v, Count ceiling) {
    if (v.size() > ceiling) {
        w.fail();
        return;
    }
    encode(w, static_cast<Count>(v.size()));
    for (size_t i = 0; i < v.size(); ++i) encode(w, v[i]);
    w.pad((static_cast<size_t>(ceiling) - v.size()) * FixedSize<T>::value);
}

/// An array whose elements are themselves arrays. Each inner array carries its own ceiling, which is
/// what makes the element a fixed size and lets the outer tail be skipped. One level is enough for
/// every FOM seen: an element that is a *record* containing an array needs nothing here, because
/// that record's own codec knows its fields' ceilings. Deeper direct nesting the generator refuses
/// rather than growing a third ceiling.
template <class T>
Result decodeBounded(Reader& r, std::vector<std::vector<T> >& v, Count ceiling, Count inner) {
    Count n = 0;
    Result rc = decode(r, n);
    if (rc != Result::Ok) return rc;

    if (n > ceiling) return Result::CorruptCount;

    v.clear();
    v.resize(n);
    for (Count i = 0; i < n; ++i) {
        rc = decodeBounded(r, v[i], inner);
        if (rc != Result::Ok) return rc;
    }

    return r.skip(static_cast<size_t>(ceiling - n) * boundedSize<T>(inner))
        ? Result::Ok
        : Result::Truncated;
}

template <class T>
void encodeBounded(Writer& w, const std::vector<std::vector<T> >& v, Count ceiling, Count inner) {
    if (v.size() > ceiling) {
        w.fail();
        return;
    }
    encode(w, static_cast<Count>(v.size()));
    for (size_t i = 0; i < v.size(); ++i) encodeBounded(w, v[i], inner);
    w.pad((static_cast<size_t>(ceiling) - v.size()) * boundedSize<T>(inner));
}

// std::vector<bool> is the packed specialisation, so v[i] is a proxy rather than a bool& and the
// templates above will not bind to it.
inline Result decodeBounded(Reader& r, std::vector<bool>& v, Count ceiling) {
    Count n = 0;
    Result rc = decode(r, n);
    if (rc != Result::Ok) return rc;

    if (n > ceiling) return Result::CorruptCount;

    v.clear();
    v.resize(n);
    for (Count i = 0; i < n; ++i) {
        bool value = false;
        rc = decode(r, value);
        if (rc != Result::Ok) return rc;
        v[i] = value;
    }

    return r.skip(static_cast<size_t>(ceiling - n) * kBooleanSize)
        ? Result::Ok
        : Result::Truncated;
}

inline void encodeBounded(Writer& w, const std::vector<bool>& v, Count ceiling) {
    if (v.size() > ceiling) {
        w.fail();
        return;
    }
    encode(w, static_cast<Count>(v.size()));
    for (size_t i = 0; i < v.size(); ++i) encode(w, static_cast<bool>(v[i]));
    w.pad((static_cast<size_t>(ceiling) - v.size()) * kBooleanSize);
}

// ---------------------------------------------------------------------------
// Datagram framing
//
//   magic       uint32  0x49434401, which lands in a dump as the ASCII "ICD" and a version byte
//   classId     uint16  the ICD's ID column, checked against what the reader expects
//   flags       uint16  bit 0 set would mean little-endian; reserved otherwise
//   recordCount uint16
//   recordSize  uint16  every record is this long — the framing, stated once
//
// Records follow back to back with nothing between them. recordSize replaces the length that used
// to precede each one: it says the same thing in two bytes rather than two per record, puts record
// k at 12 + k * recordSize, and turns a version difference into one comparison instead of a
// surprise at every record.
// ---------------------------------------------------------------------------

enum : uint32_t { kMagic = 0x49434401u };
enum : size_t { kHeaderSize = 12 };

/// Payload budget for one datagram: 1500 MTU less IP and UDP headers, less room for a VLAN tag or a
/// tunnel. kJumboPayload is the same reckoning on a 9000-byte MTU, for a path that supports it end
/// to end — worth having now that a record is as large as its ceilings make it.
enum : size_t { kDefaultPayload = 1400, kJumboPayload = 8900 };

template <class T>
class DatagramWriter {
public:
    DatagramWriter(unsigned char* buf, size_t cap, uint16_t classId)
        : buf_(buf), cap_(cap), pos_(kHeaderSize), classId_(classId), count_(0) {}

    /// How many records fit in the buffer this was given. Known before adding any, records being a
    /// fixed size.
    size_t capacityInRecords() const {
        return cap_ < kHeaderSize ? 0 : (cap_ - kHeaderSize) / FixedSize<T>::value;
    }

    /// Appends a record. Returns false when it will not fit — finish(), send, and start again.
    bool add(const T& record) {
        if (cap_ < kHeaderSize || count_ == 0xFFFFu) return false;
        if (FixedSize<T>::value > cap_ - pos_) return false;

        Writer w(buf_ + pos_, FixedSize<T>::value);
        encode(w, record);
        if (!w.ok()) return false;

        pos_ += FixedSize<T>::value;
        ++count_;
        return true;
    }

    /// Fills in the header and returns the datagram's total length.
    size_t finish() {
        if (cap_ < kHeaderSize) return 0;
        storeU32(buf_, kMagic);
        storeU16(buf_ + 4, classId_);
        storeU16(buf_ + 6, 0);
        storeU16(buf_ + 8, count_);
        storeU16(buf_ + 10, static_cast<uint16_t>(FixedSize<T>::value));
        return pos_;
    }

    uint16_t count() const { return count_; }
    bool empty() const { return count_ == 0; }

private:
    unsigned char* buf_;
    size_t cap_;
    size_t pos_;
    uint16_t classId_;
    uint16_t count_;
};

template <class T>
class DatagramReader {
public:
    DatagramReader() : buf_(0), recordSize_(0), count_(0), index_(0) {}

    /// A sender built from a later ICD has longer records; this build reads the part it knows and
    /// steps over the rest, which is what recordSize buys. Shorter records mean fields this build
    /// expects are simply absent, which cannot be papered over.
    static Result open(const unsigned char* buf, size_t len, uint16_t expectedClassId,
                       DatagramReader& out) {
        if (len < kHeaderSize) return Result::Truncated;

        // A sender using the opposite byte order sees 0x01444349 here, so this fails on the first
        // four bytes rather than after every field has silently shifted.
        if (loadU32(buf) != kMagic) return Result::BadMagic;
        if (loadU16(buf + 4) != expectedClassId) return Result::WrongClass;

        uint16_t recordSize = loadU16(buf + 10);
        if (recordSize < FixedSize<T>::value) return Result::RecordTooSmall;

        uint16_t count = loadU16(buf + 8);
        if (static_cast<size_t>(count) * recordSize > len - kHeaderSize) return Result::Truncated;

        out.buf_ = buf + kHeaderSize;
        out.recordSize_ = recordSize;
        out.count_ = count;
        out.index_ = 0;
        return Result::Ok;
    }

    uint16_t count() const { return count_; }
    uint16_t recordSize() const { return recordSize_; }
    bool hasNext() const { return index_ < count_; }

    /// Reads the next record. One that does not decode sets skipped and the reader moves on:
    /// records sit at fixed offsets, so a bad record costs that record and never the rest.
    Result next(T& out, bool& skipped) {
        skipped = false;
        if (!hasNext()) return Result::Truncated;

        Result rc = at(index_, out);
        ++index_;
        if (rc != Result::Ok) skipped = true;
        return Result::Ok;
    }

    /// Record k without reading the ones before it.
    Result at(uint16_t index, T& out) const {
        if (index >= count_) return Result::Truncated;
        Reader inner(buf_ + static_cast<size_t>(index) * recordSize_, recordSize_);
        return decode(inner, out);
    }

private:
    const unsigned char* buf_;
    uint16_t recordSize_;
    uint16_t count_;
    uint16_t index_;
};

}  // namespace icd

#endif  // ICDCODEC_H
