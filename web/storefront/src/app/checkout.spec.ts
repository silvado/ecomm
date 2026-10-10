import { newOrderToken, validCpf } from './checkout';

describe('checkout', () => {
  it('token tem 32 bytes em base64url, diferente a cada pedido', () => {
    const tokens = new Set(Array.from({ length: 50 }, () => newOrderToken()));

    expect(tokens.size).toBe(50);
    for (const token of tokens) expect(token).toMatch(/^[A-Za-z0-9_-]{43}$/);
  });

  it('CPF confere os dígitos verificadores', () => {
    expect(validCpf('123.456.789-09')).toBe(true);
    expect(validCpf('12345678909')).toBe(true);
    expect(validCpf('123.456.789-08')).toBe(false);
    expect(validCpf('123.456.789-90')).toBe(false);
    expect(validCpf('111.111.111-11')).toBe(false);
    expect(validCpf('1234567890')).toBe(false);
  });
});
