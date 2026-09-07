// icd_codec.h — runtime support for the generated ICD codecs.
//
// Hand-written and copied out verbatim by the generator; nothing here is derived from a FOM.
// C++11, no dependencies beyond the standard library, no platform headers.
//
// Byte order is little-endian, fixed. That is a property of the datagram the ICD specifies, not of
// the machine, so it is not a build option: reading a byte at a time makes it independent of the
// host's own byte order and avoids unaligned access, and compilers fold the pattern back into a
// single load. If a layout ever needs big-endian, regenerate — do not add an #ifdef.

#ifndef ICD_CODEC_H
#define ICD_CODEC_H

#include <stdint.h>
#include <stddef.h>
#include <string.h>

#include <array>
#include <vector>

namespace icd {

enum class Result {
    Ok = 0,
    Truncated,     ///< ran past the end of the buffer
    CorruptCount,  ///< an element count larger than the remaining bytes could hold
    ShortBuffer,   ///< the destination buffer cannot hold the encoding
    TooManyItems,  ///< a container holds more elements than the count field can express
    BadMagic,      ///< not an ICD datagram, or the sender's byte order differs
    WrongClass     ///< the datagram carries a different class than expected
};

/// Width of the element count that precedes a variable-length array. See DESIGN-NOTES: a datagram
/// caps the count long before 16 bits overflow, and 8 bits does not survive a single-byte-element
/// array.
typedef uint16_t Count;
enum : size_t { kCountSize = 2 };

// ---------------------------------------------------------------------------
// Raw loads and stores
// ---------------------------------------------------------------------------

inline uint8_t loadU8(const unsigned char* p) { return p[0]; }

inline uint16_t loadU16(const unsigned char* p) {
    return static_cast<uint16_t>(static_cast<unsigned>(p[0]) |
                                 (static_cast<unsigned>(p[1]) << 8));
}

inline uint32_t loadU32(const unsigned char* p) {
    return static_cast<uint32_t>(p[0]) | (static_cast<uint32_t>(p[1]) << 8) |
           (static_cast<uint32_t>(p[2]) << 16) | (static_cast<uint32_t>(p[3]) << 24);
}

inline uint64_t loadU64(const unsigned char* p) {
    return static_cast<uint64_t>(loadU32(p)) | (static_cast<uint64_t>(loadU32(p + 4)) << 32);
}

inline void storeU8(unsigned char* p, uint8_t v) { p[0] = v; }

inline void storeU16(unsigned char* p, uint16_t v) {
    p[0] = static_cast<unsigned char>(v & 0xFFu);
    p[1] = static_cast<unsigned char>((v >> 8) & 0xFFu);
}

inline void storeU32(unsigned char* p, uint32_t v) {
    p[0] = static_cast<unsigned char>(v & 0xFFu);
    p[1] = static_cast<unsigned char>((v >> 8) & 0xFFu);
    p[2] = static_cast<unsigned char>((v >> 16) & 0xFFu);
    p[3] = static_cast<unsigned char>((v >> 24) & 0xFFu);
}

inline void storeU64(unsigned char* p, uint64_t v) {
    storeU32(p, static_cast<uint32_t>(v & 0xFFFFFFFFu));
    storeU32(p + 4, static_cast<uint32_t>((v >> 32) & 0xFFFFFFFFu));
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

    /// Abandons the rest of a record whose length the sender declared. Used when a record decodes
    /// short (an older receiver reading a newer sender) or has to be skipped whole.
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
// Minimum encoded size
//
// What a value occupies with every variable-length part empty. It is the divisor that bounds an
// element count against the bytes actually left, so it must never be zero. Generated records carry
// kMinEncodedSize; the primary template picks it up.
// ---------------------------------------------------------------------------

template <class T>
struct MinSize {
    enum : size_t { value = T::kMinEncodedSize };
};

template <> struct MinSize<uint8_t>  { enum : size_t { value = 1 }; };
template <> struct MinSize<uint16_t> { enum : size_t { value = 2 }; };
template <> struct MinSize<uint32_t> { enum : size_t { value = 4 }; };
template <> struct MinSize<uint64_t> { enum : size_t { value = 8 }; };
template <> struct MinSize<int8_t>   { enum : size_t { value = 1 }; };
template <> struct MinSize<int16_t>  { enum : size_t { value = 2 }; };
template <> struct MinSize<int32_t>  { enum : size_t { value = 4 }; };
template <> struct MinSize<int64_t>  { enum : size_t { value = 8 }; };
template <> struct MinSize<float>    { enum : size_t { value = 4 }; };
template <> struct MinSize<double>   { enum : size_t { value = 8 }; };

template <class T>
struct MinSize<std::vector<T> > {
    enum : size_t { value = kCountSize };
};

template <class T, size_t N>
struct MinSize<std::array<T, N> > {
    enum : size_t { value = N * MinSize<T>::value };
};

// ---------------------------------------------------------------------------
// Primitives
// ---------------------------------------------------------------------------

#define ICD_PRIMITIVE(TYPE, WIDTH, LOAD, STORE)                       \
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

ICD_PRIMITIVE(uint8_t, 1, loadU8, storeU8)
ICD_PRIMITIVE(uint16_t, 2, loadU16, storeU16)
ICD_PRIMITIVE(uint32_t, 4, loadU32, storeU32)
ICD_PRIMITIVE(uint64_t, 8, loadU64, storeU64)
ICD_PRIMITIVE(int8_t, 1, loadU8, storeU8)
ICD_PRIMITIVE(int16_t, 2, loadU16, storeU16)
ICD_PRIMITIVE(int32_t, 4, loadU32, storeU32)
ICD_PRIMITIVE(int64_t, 8, loadU64, storeU64)
ICD_PRIMITIVE(float, 4, loadF32, storeF32)
ICD_PRIMITIVE(double, 8, loadF64, storeF64)

#undef ICD_PRIMITIVE

// ---------------------------------------------------------------------------
// Containers
//
// These are templates so that nesting costs nothing to support: a vector of records containing
// vectors is the same code one level down. Calls to decode/encode for the element type resolve at
// instantiation, so a generated type only has to declare its own overloads before use.
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
size_t encodedSize(const std::array<T, N>& v) {
    size_t total = 0;
    for (size_t i = 0; i < N; ++i) total += encodedSize(v[i]);
    return total;
}

template <class T>
Result decode(Reader& r, std::vector<T>& v) {
    // MinSize is the divisor below; a zero would divide by zero rather than reject anything.
    static_assert(MinSize<T>::value > 0, "element minimum size must be positive");

    Count n = 0;
    Result rc = decode(r, n);
    if (rc != Result::Ok) return rc;

    // Divide, never multiply: n * MinSize can wrap and let an impossible count through. The bytes
    // left are the authority on how many elements can exist, so no configured maximum is needed.
    if (n > r.remaining() / MinSize<T>::value) return Result::CorruptCount;

    v.clear();
    v.resize(n);
    for (Count i = 0; i < n; ++i) {
        rc = decode(r, v[i]);
        if (rc != Result::Ok) return rc;
    }
    return Result::Ok;
}

template <class T>
void encode(Writer& w, const std::vector<T>& v) {
    if (v.size() > 0xFFFFu) {
        w.fail();
        return;
    }
    encode(w, static_cast<Count>(v.size()));
    for (size_t i = 0; i < v.size(); ++i) encode(w, v[i]);
}

template <class T>
size_t encodedSize(const std::vector<T>& v) {
    size_t total = kCountSize;
    for (size_t i = 0; i < v.size(); ++i) total += encodedSize(v[i]);
    return total;
}

// ---------------------------------------------------------------------------
// Datagram framing
//
//   magic       uint32  0x49434401
//   classId     uint16  the ICD's ID column, checked against what the reader expects
//   flags       uint16  bit 0 set would mean big-endian; reserved otherwise
//   recordCount uint16
//   reserved    uint16
//   then recordCount times: recordLen uint16, followed by the record body
//
// The per-record length is what lets one corrupt record be skipped instead of losing the whole
// packet, and what lets a receiver built from an older ICD read a record that has grown.
// ---------------------------------------------------------------------------

enum : uint32_t { kMagic = 0x49434401u };
enum : size_t { kHeaderSize = 12, kRecordLenSize = 2 };

/// Payload budget for one datagram: 1500 MTU less IP and UDP headers, less room for a VLAN tag or
/// a tunnel. Raise it only for a path you control end to end.
enum : size_t { kDefaultPayload = 1400 };

template <class T>
class DatagramWriter {
public:
    DatagramWriter(unsigned char* buf, size_t cap, uint16_t classId)
        : buf_(buf), cap_(cap), pos_(kHeaderSize), classId_(classId), count_(0) {}

    /// Appends a record. Returns false when it will not fit — finish(), send, and start again.
    bool add(const T& record) {
        if (cap_ < kHeaderSize) return false;
        if (count_ == 0xFFFFu) return false;

        size_t body = encodedSize(record);
        if (body > 0xFFFFu) return false;

        size_t need = kRecordLenSize + body;
        if (need > cap_ - pos_) return false;

        Writer w(buf_ + pos_, need);
        encode(w, static_cast<uint16_t>(body));
        encode(w, record);
        if (!w.ok()) return false;

        pos_ += need;
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
        storeU16(buf_ + 10, 0);
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
    DatagramReader() : reader_(0, 0), count_(0), index_(0) {}

    static Result open(const unsigned char* buf, size_t len, uint16_t expectedClassId,
                       DatagramReader& out) {
        if (len < kHeaderSize) return Result::Truncated;

        // A sender using the opposite byte order sees 0x01444349 here, so this fails on the first
        // four bytes rather than after every field has silently shifted.
        if (loadU32(buf) != kMagic) return Result::BadMagic;
        if (loadU16(buf + 4) != expectedClassId) return Result::WrongClass;

        out.reader_ = Reader(buf + kHeaderSize, len - kHeaderSize);
        out.count_ = loadU16(buf + 8);
        out.index_ = 0;
        return Result::Ok;
    }

    uint16_t count() const { return count_; }
    bool hasNext() const { return index_ < count_; }

    /// Reads the next record. A record that does not decode sets skipped and the reader moves on to
    /// the next one, so one bad record costs one record rather than the datagram.
    Result next(T& out, bool& skipped) {
        skipped = false;
        if (!hasNext()) return Result::Truncated;
        ++index_;

        uint16_t bodyLen = 0;
        Result rc = decode(reader_, bodyLen);
        if (rc != Result::Ok) return rc;

        const unsigned char* body = reader_.take(bodyLen);
        if (!body) return Result::Truncated;

        Reader inner(body, bodyLen);
        rc = decode(inner, out);
        if (rc != Result::Ok) {
            skipped = true;
            return Result::Ok;
        }

        // Bytes left over mean the sender was built from a later ICD that appended fields. Reading
        // what we know and ignoring the tail is the whole point of carrying a length.
        return Result::Ok;
    }

private:
    Reader reader_;
    uint16_t count_;
    uint16_t index_;
};

}  // namespace icd

#endif  // ICD_CODEC_H
