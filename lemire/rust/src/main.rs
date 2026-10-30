use smartstring::alias::String as SmartString;
use std::hint::black_box;
use std::time::Instant;

#[inline(never)]
fn itoa_to_string(buf: &mut [String], n: u64) {
    let mut b = itoa::Buffer::new();
    for i in 0..n {
        buf[(i & 1023) as usize] = b.format(i).to_string();
    }
}

#[inline(never)]
fn itoa_to_smartstring(buf: &mut [SmartString], n: u64) {
    let mut b = itoa::Buffer::new();
    for i in 0..n {
        buf[(i & 1023) as usize] = b.format(i).into();
    }
}

fn bench<S: Default + Clone + std::ops::Deref<Target = str>>(name: &str, f: fn(&mut [S], u64)) {
    let n: u64 = 100_000_000;
    let mut buf = vec![S::default(); 1024];
    f(&mut buf, 1_000_000); // warm up
    let mut best = f64::INFINITY;
    for _ in 0..5 {
        let t0 = Instant::now();
        f(black_box(&mut buf), black_box(n));
        let dt = t0.elapsed().as_nanos() as f64;
        if dt < best {
            best = dt;
        }
    }
    let total: usize = buf.iter().map(|s| s.len()).sum();
    let ns = best / n as f64;
    println!(
        "{:<24} {:7.2} ns/string  {:8.1} M/s   (check {})",
        name,
        ns,
        1e3 / ns,
        total
    );
}

fn main() {
    println!("Rust");
    bench("itoa + string", itoa_to_string);
    bench("itoa + smartstring", itoa_to_smartstring);
}
