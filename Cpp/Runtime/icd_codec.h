// icd_codec.h — runtime support for the generated ICD codecs.
//
// Hand-written and copied out verbatim by the generator; nothing here is derived from a FOM.
// C++17, no dependencies beyond the standard library, no platform headers.
//
// Every record is a fixed-size box. A dynamic array occupies its ceiling whatever it actually
// carries — a count, then room for the agreed maximum, the tail zero-filled — so a class always
// encodes to the same number of bytes. That is what makes the size a constant, lets a datagram
// state "N records of M bytes" once in its header, and puts record k at a computable offset. It
// costs the unused tail of every array, which is the trade the ceilings exist to make.
//
// Byte order is big-endian, agreed rather than negotiated: no flag says so and nothing checks. The
// six load/store functions are the only place it is decided, and nothing in the generated tree
// encodes an order, so reversing it is an edit to those six and no regeneration. Do not add an
// #ifdef.
//
// Identifiers here deliberately avoid the prefix that generated type names can carry. That prefix
// exists so a project can search-and-replace it and point these codecs at its own HLA-generated
// type definitions; an include guard or macro caught by that replace would be a puzzling breakage,
// so the token appears nowhere in this file.

#ifndef ICDCODEC_H
#define ICDCODEC_H

#include <cstddef>
#include <cstdint>
#include <cstring>

#include <array>
#include <vector>

namespace icd {

enum class Result {
    Ok = 0,
    Truncated,      ///< ran past the end of the buffer
    CorruptCount,   ///< an element count above the array's agreed ceiling
    ShortBuffer,    ///< the destination buffer cannot hold the encoding
    TooManyItems,   ///< a container holds more elements than its ceiling allows
    WrongClass,     ///< the datagram carries a different class than expected
    RecordTooSmall  ///< the sender's records are shorter than this build expects
};

/// Width of the element count that precedes a variable-length array. A datagram caps the count long
/// before 16 bits overflow, and 8 bits does not survive an array of single-byte elements.
using Count = std::uint16_t;
inline constexpr std::size_t kCountSize = 2;

/// HLAboolean. The standard MIM encodes it over HLAinteger32BE, but that is HLA's encoding and this
/// datagram is not HLA's; a boolean here is one byte. A FOM declaring a boolean of its own keeps
/// whatever width it states.
inline constexpr std::size_t kBooleanSize = 1;

// ---------------------------------------------------------------------------
// Raw loads and stores
//
// The six multi-byte functions below are the only place an order is decided. Reading a byte at a
// time makes them independent of the host's own order and immune to unaligned access; compilers
// fold the pattern back into a single load plus a bswap.
// ---------------------------------------------------------------------------

[[nodiscard]] inline std::uint8_t loadU8(const unsigned char* p) { return p[0]; }

[[nodiscard]] inline std::uint16_t loadU16(const unsigned char* p) {
    return static_cast<std::uint16_t>((static_cast<unsigned>(p[0]) << 8) |
                                      static_cast<unsigned>(p[1]));
}

[[nodiscard]] inline std::uint32_t loadU32(const unsigned char* p) {
    return (static_cast<std::uint32_t>(p[0]) << 24) | (static_cast<std::uint32_t>(p[1]) << 16) |
           (static_cast<std::uint32_t>(p[2]) << 8) | static_cast<std::uint32_t>(p[3]);
}

[[nodiscard]] inline std::uint64_t loadU64(const unsigned char* p) {
    return (static_cast<std::uint64_t>(loadU32(p)) << 32) | static_cast<std::uint64_t>(loadU32(p + 4));
}

inline void storeU8(unsigned char* p, std::uint8_t v) { p[0] = v; }

inline void storeU16(unsigned char* p, std::uint16_t v) {
    p[0] = static_cast<unsigned char>((v >> 8) & 0xFFu);
    p[1] = static_cast<unsigned char>(v & 0xFFu);
}

inline void storeU32(unsigned char* p, std::uint32_t v) {
    p[0] = static_cast<unsigned char>((v >> 24) & 0xFFu);
    p[1] = static_cast<unsigned char>((v >> 16) & 0xFFu);
    p[2] = static_cast<unsigned char>((v >> 8) & 0xFFu);
    p[3] = static_cast<unsigned char>(v & 0xFFu);
}

inline void storeU64(unsigned char* p, std::uint64_t v) {
    storeU32(p, static_cast<std::uint32_t>((v >> 32) & 0xFFFFFFFFu));
    storeU32(p + 4, static_cast<std::uint32_t>(v & 0xFFFFFFFFu));
}

// Floating point goes through memcpy. A union or a reinterpret_cast violates strict aliasing and
// does break under -O2; memcpy is the only conforming spelling and compilers reduce it to a move.
[[nodiscard]] inline float loadF32(const unsigned char* p) {
    const std::uint32_t bits = loadU32(p);
    float value;
    std::memcpy(&value, &bits, sizeof value);
    return value;
}

[[nodiscard]] inline double loadF64(const unsigned char* p) {
    const std::uint64_t bits = loadU64(p);
    double value;
    std::memcpy(&value, &bits, sizeof value);
    return value;
}

inline void storeF32(unsigned char* p, float value) {
    std::uint32_t bits;
    std::memcpy(&bits, &value, sizeof bits);
    storeU32(p, bits);
}

inline void storeF64(unsigned char* p, double value) {
    std::uint64_t bits;
    std::memcpy(&bits, &value, sizeof bits);
    storeU64(p, bits);
}

// ---------------------------------------------------------------------------
// Bounds-checked cursors
// ---------------------------------------------------------------------------

/// Walks a buffer, refusing to hand out more than is left. Every read goes through take(), so a
/// truncated or hostile datagram cannot walk off the end.
class Reader {
public:
    Reader(const unsigned char* buf, std::size_t len) noexcept : at_(buf), end_(buf + len) {}

    [[nodiscard]] std::size_t remaining() const noexcept {
        return static_cast<std::size_t>(end_ - at_);
    }

    /// Returns nullptr rather than a short block when the bytes are not there.
    [[nodiscard]] const unsigned char* take(std::size_t n) noexcept {
        if (remaining() < n) return nullptr;
        const unsigned char* start = at_;
        at_ += n;
        return start;
    }

    /// Steps over bytes without reading them — an array's unused tail, or a field this build does
    /// not know about because the sender was generated from a later ICD.
    [[nodiscard]] bool skip(std::size_t n) noexcept { return take(n) != nullptr; }

private:
    const unsigned char* at_;
    const unsigned char* end_;
};

/// The writing counterpart. A failed write is latched rather than reported at every call site, so
/// generated encoders stay free of error checks; ok() is consulted once at the end.
class Writer {
public:
    Writer(unsigned char* buf, std::size_t cap) noexcept
        : begin_(buf), at_(buf), end_(buf + cap) {}

    [[nodiscard]] unsigned char* take(std::size_t n) noexcept {
        if (static_cast<std::size_t>(end_ - at_) < n) {
            ok_ = false;
            return nullptr;
        }
        unsigned char* start = at_;
        at_ += n;
        return start;
    }

    /// Fills an array's unused tail. Zeros rather than whatever the buffer held, so two encodings of
    /// the same value are byte-identical and two captures can be diffed.
    void pad(std::size_t n) noexcept {
        if (unsigned char* p = take(n)) std::memset(p, 0, n);
    }

    void fail() noexcept { ok_ = false; }
    [[nodiscard]] bool ok() const noexcept { return ok_; }
    [[nodiscard]] std::size_t written() const noexcept {
        return static_cast<std::size_t>(at_ - begin_);
    }

private:
    unsigned char* begin_;
    unsigned char* at_;
    unsigned char* end_;
    bool ok_ = true;
};

// ---------------------------------------------------------------------------
// Encoded size
//
// Every type occupies a constant number of bytes, arrays included — they are written at their
// ceiling. Generated records carry kEncodedSize and the primary template picks it up.
//
// There used to be an encodedSize(value) overload beside every decode/encode pair, from when a
// record's length depended on what was in it. Fixed-size records made all of them return a
// constant and ignore their argument, and nothing called them; fixedSize<T> answers the same
// question without needing a value, which is what the runtime itself needs — how many records fit
// is worked out before there is a record to ask. wireSize(value) is the one function left for the
// times a caller has a value rather than a type name.
// ---------------------------------------------------------------------------

template <class T>
struct FixedSize {
    static constexpr std::size_t value = T::kEncodedSize;
};

template <> struct FixedSize<std::uint8_t>  { static constexpr std::size_t value = 1; };
template <> struct FixedSize<std::uint16_t> { static constexpr std::size_t value = 2; };
template <> struct FixedSize<std::uint32_t> { static constexpr std::size_t value = 4; };
template <> struct FixedSize<std::uint64_t> { static constexpr std::size_t value = 8; };
template <> struct FixedSize<std::int8_t>   { static constexpr std::size_t value = 1; };
template <> struct FixedSize<std::int16_t>  { static constexpr std::size_t value = 2; };
template <> struct FixedSize<std::int32_t>  { static constexpr std::size_t value = 4; };
template <> struct FixedSize<std::int64_t>  { static constexpr std::size_t value = 8; };
template <> struct FixedSize<float>         { static constexpr std::size_t value = 4; };
template <> struct FixedSize<double>        { static constexpr std::size_t value = 8; };
template <> struct FixedSize<bool>          { static constexpr std::size_t value = kBooleanSize; };

template <class T, std::size_t N>
struct FixedSize<std::array<T, N>> {
    static constexpr std::size_t value = N * FixedSize<T>::value;
};

/// Shorthand, so a size reads as a value rather than a member lookup.
template <class T>
inline constexpr std::size_t fixedSize = FixedSize<T>::value;

/// The same number for a value rather than a type name, so a caller holding a record does not have
/// to spell out decltype and strip the reference off it. Not sizeof: this is the byte count on the
/// wire, which for anything holding a std::vector is nothing like the object's own size.
template <class T>
[[nodiscard]] constexpr std::size_t wireSize(const T&) noexcept {
    return fixedSize<T>;
}

// ---------------------------------------------------------------------------
// Primitives
// ---------------------------------------------------------------------------

#define ICDCODEC_PRIMITIVE(TYPE, WIDTH, LOAD, STORE)                              \
    [[nodiscard]] inline Result decode(Reader& r, TYPE& v) noexcept {             \
        const unsigned char* p = r.take(WIDTH);                                   \
        if (!p) return Result::Truncated;                                         \
        v = static_cast<TYPE>(LOAD(p));                                           \
        return Result::Ok;                                                        \
    }                                                                             \
    inline void encode(Writer& w, TYPE v) noexcept {                              \
        if (unsigned char* p = w.take(WIDTH)) STORE(p, v);                        \
    }

ICDCODEC_PRIMITIVE(std::uint8_t, 1, loadU8, storeU8)
ICDCODEC_PRIMITIVE(std::uint16_t, 2, loadU16, storeU16)
ICDCODEC_PRIMITIVE(std::uint32_t, 4, loadU32, storeU32)
ICDCODEC_PRIMITIVE(std::uint64_t, 8, loadU64, storeU64)
ICDCODEC_PRIMITIVE(std::int8_t, 1, loadU8, storeU8)
ICDCODEC_PRIMITIVE(std::int16_t, 2, loadU16, storeU16)
ICDCODEC_PRIMITIVE(std::int32_t, 4, loadU32, storeU32)
ICDCODEC_PRIMITIVE(std::int64_t, 8, loadU64, storeU64)
ICDCODEC_PRIMITIVE(float, 4, loadF32, storeF32)
ICDCODEC_PRIMITIVE(double, 8, loadF64, storeF64)

#undef ICDCODEC_PRIMITIVE

// Any non-zero encoding reads as true. The wire carries a width, not a C++ bool, and a sender that
// writes something other than 0 or 1 has still said "true".
[[nodiscard]] inline Result decode(Reader& r, bool& v) noexcept {
    std::uint8_t raw = 0;
    if (const Result rc = decode(r, raw); rc != Result::Ok) return rc;
    v = raw != 0;
    return Result::Ok;
}

inline void encode(Writer& w, bool v) noexcept {
    encode(w, static_cast<std::uint8_t>(v ? 1 : 0));
}

// ---------------------------------------------------------------------------
// Fixed-length arrays
// ---------------------------------------------------------------------------

template <class T, std::size_t N>
[[nodiscard]] Result decode(Reader& r, std::array<T, N>& v) {
    for (std::size_t i = 0; i < N; ++i) {
        if (const Result rc = decode(r, v[i]); rc != Result::Ok) return rc;
    }
    return Result::Ok;
}

template <class T, std::size_t N>
void encode(Writer& w, const std::array<T, N>& v) {
    for (std::size_t i = 0; i < N; ++i) encode(w, v[i]);
}

// ---------------------------------------------------------------------------
// Variable-length arrays
//
// A count, then room for `ceiling` elements with the tail zero-filled, so the field is the same
// size every time. The ceiling is an argument rather than part of the type: it is a federation
// agreement attached to the array's type, and the container template cannot carry it. The generator
// passes the value it read from the array limits.
// ---------------------------------------------------------------------------

/// Bytes a bounded array occupies, whatever it currently holds.
template <class T>
[[nodiscard]] constexpr std::size_t boundedSize(Count ceiling) noexcept {
    return kCountSize + static_cast<std::size_t>(ceiling) * fixedSize<T>;
}

template <class T>
[[nodiscard]] Result decodeBounded(Reader& r, std::vector<T>& v, Count ceiling) {
    Count n = 0;
    if (const Result rc = decode(r, n); rc != Result::Ok) return rc;

    // The ceiling is the authority, not the bytes left: the field is that long by construction, so
    // a larger count is corruption rather than a longer record.
    if (n > ceiling) return Result::CorruptCount;

    v.clear();
    v.resize(n);
    for (Count i = 0; i < n; ++i) {
        if (const Result rc = decode(r, v[i]); rc != Result::Ok) return rc;
    }

    return r.skip(static_cast<std::size_t>(ceiling - n) * fixedSize<T>)
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
    for (const T& item : v) encode(w, item);
    w.pad((static_cast<std::size_t>(ceiling) - v.size()) * fixedSize<T>);
}

/// An array whose elements are themselves arrays. Each inner array carries its own ceiling, which is
/// what makes the element a fixed size and lets the outer tail be skipped. One level is enough for
/// every FOM seen: an element that is a *record* containing an array needs nothing here, because
/// that record's own codec knows its fields' ceilings. Deeper direct nesting the generator refuses
/// rather than growing a third ceiling.
template <class T>
[[nodiscard]] Result decodeBounded(Reader& r, std::vector<std::vector<T>>& v,
                                   Count ceiling, Count inner) {
    Count n = 0;
    if (const Result rc = decode(r, n); rc != Result::Ok) return rc;

    if (n > ceiling) return Result::CorruptCount;

    v.clear();
    v.resize(n);
    for (Count i = 0; i < n; ++i) {
        if (const Result rc = decodeBounded(r, v[i], inner); rc != Result::Ok) return rc;
    }

    return r.skip(static_cast<std::size_t>(ceiling - n) * boundedSize<T>(inner))
        ? Result::Ok
        : Result::Truncated;
}

template <class T>
void encodeBounded(Writer& w, const std::vector<std::vector<T>>& v, Count ceiling, Count inner) {
    if (v.size() > ceiling) {
        w.fail();
        return;
    }
    encode(w, static_cast<Count>(v.size()));
    for (const std::vector<T>& item : v) encodeBounded(w, item, inner);
    w.pad((static_cast<std::size_t>(ceiling) - v.size()) * boundedSize<T>(inner));
}

// std::vector<bool> is the packed specialisation, so v[i] is a proxy rather than a bool& and the
// templates above will not bind to it.
[[nodiscard]] inline Result decodeBounded(Reader& r, std::vector<bool>& v, Count ceiling) {
    Count n = 0;
    if (const Result rc = decode(r, n); rc != Result::Ok) return rc;

    if (n > ceiling) return Result::CorruptCount;

    v.clear();
    v.resize(n);
    for (Count i = 0; i < n; ++i) {
        bool value = false;
        if (const Result rc = decode(r, value); rc != Result::Ok) return rc;
        v[i] = value;
    }

    return r.skip(static_cast<std::size_t>(ceiling - n) * kBooleanSize)
        ? Result::Ok
        : Result::Truncated;
}

inline void encodeBounded(Writer& w, const std::vector<bool>& v, Count ceiling) {
    if (v.size() > ceiling) {
        w.fail();
        return;
    }
    encode(w, static_cast<Count>(v.size()));
    for (const bool item : v) encode(w, item);
    w.pad((static_cast<std::size_t>(ceiling) - v.size()) * kBooleanSize);
}

// ---------------------------------------------------------------------------
// Datagram framing
//
//   classId     uint32  +0   the ICD's ID column, checked against what the reader expects
//   recordCount uint32  +4
//   recordSize  uint32  +8   every record is this long — the framing, stated once
//
// Twelve bytes, then the records back to back with nothing between them. There is no magic word and
// no byte-order flag: the order is agreed rather than negotiated, and classId already catches the
// realistic failure, which is a port pointed at the wrong stream. A sender using the opposite order
// would put a wildly different value in classId and be rejected there, though not as legibly.
// ---------------------------------------------------------------------------

inline constexpr std::size_t kHeaderSize = 12;

/// Payload budget for one datagram: 1500 MTU less IP and UDP headers, less room for a VLAN tag or a
/// tunnel. kJumboPayload is the same reckoning on a 9000-byte MTU, for a path that supports it end
/// to end — worth having now that a record is as large as its ceilings make it.
inline constexpr std::size_t kDefaultPayload = 1400;
inline constexpr std::size_t kJumboPayload = 8900;

template <class T>
class DatagramWriter {
public:
    DatagramWriter(unsigned char* buf, std::size_t cap, std::uint32_t classId) noexcept
        : buf_(buf), cap_(cap), classId_(classId) {}

    /// How many records fit in the buffer this was given. Known before adding any, records being a
    /// fixed size.
    [[nodiscard]] std::size_t capacityInRecords() const noexcept {
        return cap_ < kHeaderSize ? 0 : (cap_ - kHeaderSize) / fixedSize<T>;
    }

    /// Appends a record. Returns false when it will not fit — finish(), send, and start again.
    [[nodiscard]] bool add(const T& record) {
        if (cap_ < kHeaderSize) return false;
        if (fixedSize<T> > cap_ - pos_) return false;

        Writer w(buf_ + pos_, fixedSize<T>);
        encode(w, record);
        if (!w.ok()) return false;

        pos_ += fixedSize<T>;
        ++count_;
        return true;
    }

    /// Fills in the header and returns the datagram's total length.
    [[nodiscard]] std::size_t finish() noexcept {
        if (cap_ < kHeaderSize) return 0;
        storeU32(buf_, classId_);
        storeU32(buf_ + 4, count_);
        storeU32(buf_ + 8, static_cast<std::uint32_t>(fixedSize<T>));
        return pos_;
    }

    [[nodiscard]] std::uint32_t count() const noexcept { return count_; }
    [[nodiscard]] bool empty() const noexcept { return count_ == 0; }

private:
    unsigned char* buf_;
    std::size_t cap_;
    std::size_t pos_ = kHeaderSize;
    std::uint32_t classId_;
    std::uint32_t count_ = 0;
};

template <class T>
class DatagramReader {
public:
    DatagramReader() noexcept = default;

    /// A sender built from a later ICD has longer records; this build reads the part it knows and
    /// steps over the rest, which is what recordSize buys. Shorter records mean fields this build
    /// expects are simply absent, which cannot be papered over.
    [[nodiscard]] static Result open(const unsigned char* buf, std::size_t len,
                                     std::uint32_t expectedClassId, DatagramReader& out) noexcept {
        if (len < kHeaderSize) return Result::Truncated;
        if (loadU32(buf) != expectedClassId) return Result::WrongClass;

        const std::uint32_t recordSize = loadU32(buf + 8);
        if (recordSize < fixedSize<T>) return Result::RecordTooSmall;

        const std::uint32_t count = loadU32(buf + 4);
        if (static_cast<std::size_t>(count) * recordSize > len - kHeaderSize) {
            return Result::Truncated;
        }

        out.buf_ = buf + kHeaderSize;
        out.recordSize_ = recordSize;
        out.count_ = count;
        out.index_ = 0;
        return Result::Ok;
    }

    [[nodiscard]] std::uint32_t count() const noexcept { return count_; }
    [[nodiscard]] std::uint32_t recordSize() const noexcept { return recordSize_; }
    [[nodiscard]] bool hasNext() const noexcept { return index_ < count_; }

    /// Reads the next record. One that does not decode sets skipped and the reader moves on:
    /// records sit at fixed offsets, so a bad record costs that record and never the rest.
    [[nodiscard]] Result next(T& out, bool& skipped) {
        skipped = false;
        if (!hasNext()) return Result::Truncated;

        const Result rc = at(index_, out);
        ++index_;
        if (rc != Result::Ok) skipped = true;
        return Result::Ok;
    }

    /// Record k without reading the ones before it.
    [[nodiscard]] Result at(std::uint32_t index, T& out) const {
        if (index >= count_) return Result::Truncated;
        Reader inner(buf_ + static_cast<std::size_t>(index) * recordSize_, recordSize_);
        return decode(inner, out);
    }

private:
    const unsigned char* buf_ = nullptr;
    std::uint32_t recordSize_ = 0;
    std::uint32_t count_ = 0;
    std::uint32_t index_ = 0;
};

}  // namespace icd

#endif  // ICDCODEC_H
