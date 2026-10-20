import { describe, expect, it } from 'vitest';
import { ApiError } from './api/client';
import { ballot, parseIds, percent, stance } from './format';

describe('format helpers', () => {
  it('shows ballots and stances as words, and leaves other values alone', () => {
    expect(ballot('Y')).toBe('YES');
    expect(ballot('A')).toBe('ABSTAIN');
    expect(ballot('Maritime Safety')).toBe('Maritime Safety');
    expect(stance('For')).toBe('YES');
    expect(stance('Against')).toBe('NO');
  });

  it('computes progress without dividing by zero', () => {
    expect(percent(312, 598)).toBe(52);
    expect(percent(5, null)).toBe(0);
    expect(percent(900, 600)).toBe(100);
  });

  it('splits a pasted list of vote ids and removes duplicates', () => {
    expect(parseIds('MA/2024/101, MA/2024/102\nMA/2024/101  MA/2024/103')).toEqual(['MA/2024/101', 'MA/2024/102', 'MA/2024/103']);
  });
});

describe('ApiError', () => {
  const error = new ApiError(400, { title: 'Validation', errors: { Weight: ['The weight must be 1 to 5.'], other: ['x'] } });

  it('finds a field error whatever the case of the key (POS-02: the message goes next to the field)', () => {
    expect(error.fieldError('weight')).toBe('The weight must be 1 to 5.');
    expect(error.fieldError('stance')).toBeNull();
  });

  it('lists the messages that belong to no field on the screen', () => {
    expect(error.unmatched(['weight'])).toEqual(['other: x']);
  });

  it('knows a 409 is a conflict with the current state, not a failure', () => {
    expect(new ApiError(409, { title: 'Position changed' }).isConflict).toBe(true);
    expect(error.isConflict).toBe(false);
  });
});
