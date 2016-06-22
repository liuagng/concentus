﻿using Concentus.Celt.Enums;
using Concentus.Common;
using Concentus.Common.CPlusPlus;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;


namespace Concentus.Celt
{
    public static class pitch
    {
        public static void find_best_pitch(Pointer<int> xcorr, Pointer<int> y, int len,
                                    int max_pitch, Pointer<int> best_pitch,
                                    int yshift, int maxcorr
                                    )
        {
            int i, j;
            int Syy = 1;
            Pointer<int> best_num = Pointer.Malloc<int>(2);
            Pointer<int> best_den = Pointer.Malloc<int>(2);
            int xshift = Inlines.celt_ilog2(maxcorr) - 14;

            best_num[0] = -1;
            best_num[1] = -1;
            best_den[0] = 0;
            best_den[1] = 0;
            best_pitch[0] = 0;
            best_pitch[1] = 1;
            for (j = 0; j < len; j++)
                Syy = Inlines.ADD32(Syy, Inlines.SHR32(Inlines.MULT16_16(y[j], y[j]), yshift));
            for (i = 0; i < max_pitch; i++)
            {
                if (xcorr[i] > 0)
                {
                    int num;
                    int xcorr16;
                    xcorr16 = Inlines.EXTRACT16(Inlines.VSHR32(xcorr[i], xshift));
                    num = Inlines.MULT16_16_Q15((xcorr16), (xcorr16));
                    if (Inlines.MULT16_32_Q15(num, best_den[1]) > Inlines.MULT16_32_Q15(best_num[1], Syy))
                    {
                        if (Inlines.MULT16_32_Q15(num, best_den[0]) > Inlines.MULT16_32_Q15(best_num[0], Syy))
                        {
                            best_num[1] = best_num[0];
                            best_den[1] = best_den[0];
                            best_pitch[1] = best_pitch[0];
                            best_num[0] = num;
                            best_den[0] = Syy;
                            best_pitch[0] = i;
                        }
                        else
                        {
                            best_num[1] = num;
                            best_den[1] = Syy;
                            best_pitch[1] = i;
                        }
                    }
                }

                Syy += Inlines.SHR32(Inlines.MULT16_16(y[i + len], y[i + len]), yshift) - Inlines.SHR32(Inlines.MULT16_16(y[i], y[i]), yshift);
                Syy = Inlines.MAX32(1, Syy);
            }
        }

        public static void celt_fir5(Pointer<int> x,
                Pointer<int> num,
                Pointer<int> y,
                int N,
                Pointer<int> mem)
        {
            int i;
            int num0, num1, num2, num3, num4;
            int mem0, mem1, mem2, mem3, mem4;
            num0 = num[0];
            num1 = num[1];
            num2 = num[2];
            num3 = num[3];
            num4 = num[4];
            mem0 = mem[0];
            mem1 = mem[1];
            mem2 = mem[2];
            mem3 = mem[3];
            mem4 = mem[4];
            for (i = 0; i < N; i++)
            {
                int sum = Inlines.SHL32(Inlines.EXTEND32(x[i]), CeltConstants.SIG_SHIFT);
                sum = Inlines.MAC16_16(sum, num0, (mem0));
                sum = Inlines.MAC16_16(sum, num1, (mem1));
                sum = Inlines.MAC16_16(sum, num2, (mem2));
                sum = Inlines.MAC16_16(sum, num3, (mem3));
                sum = Inlines.MAC16_16(sum, num4, (mem4));
                mem4 = mem3;
                mem3 = mem2;
                mem2 = mem1;
                mem1 = mem0;
                mem0 = x[i];
                y[i] = Inlines.ROUND16(sum, CeltConstants.SIG_SHIFT);
            }
            mem[0] = (mem0);
            mem[1] = (mem1);
            mem[2] = (mem2);
            mem[3] = (mem3);
            mem[4] = (mem4);
        }


        public static void pitch_downsample(Pointer<Pointer<int>> x, Pointer<int> x_lp, int len, int C, int arch)
        {
            int i;
            int[] ac = new int[5];
            int tmp = CeltConstants.Q15ONE;
            int[] lpc = new int[4];
            int[] mem = new int[] { 0, 0, 0, 0, 0 };
            int[] lpc2 = new int[5];
            int c1 = Inlines.QCONST16(0.8f, 15);

            int shift;
            int maxabs = Inlines.celt_maxabs32(x[0], len);
            if (C == 2)
            {
                int maxabs_1 = Inlines.celt_maxabs32(x[1], len);
                maxabs = Inlines.MAX32(maxabs, maxabs_1);
            }
            if (maxabs < 1)
                maxabs = 1;
            shift = Inlines.celt_ilog2(maxabs) - 10;
            if (shift < 0)
                shift = 0;
            if (C == 2)
                shift++;

            for (i = 1; i < len >> 1; i++)
            {
                x_lp[i] = (Inlines.SHR32(Inlines.HALF32(Inlines.HALF32(x[0][(2 * i - 1)] + x[0][(2 * i + 1)]) + x[0][2 * i]), shift));
            }

            x_lp[0] = (Inlines.SHR32(Inlines.HALF32(Inlines.HALF32(x[0][1]) + x[0][0]), shift));

            if (C == 2)
            {
                for (i = 1; i < len >> 1; i++)
                    x_lp[i] += (Inlines.SHR32(Inlines.HALF32(Inlines.HALF32(x[1][(2 * i - 1)] + x[1][(2 * i + 1)]) + x[1][2 * i]), shift));
                x_lp[0] += (Inlines.SHR32(Inlines.HALF32(Inlines.HALF32(x[1][1]) + x[1][0]), shift));
            }

            celt_lpc._celt_autocorr(x_lp, ac.GetPointer(), null, 0,
                           4, len >> 1, arch);

            /* Noise floor -40 dB */
            ac[0] += Inlines.SHR32(ac[0], 13);
            /* Lag windowing */
            for (i = 1; i <= 4; i++)
            {
                /*ac[i] *= exp(-.5*(2*M_PI*.002*i)*(2*M_PI*.002*i));*/
                ac[i] -= Inlines.MULT16_32_Q15((2 * i * i), ac[i]);
            }

            celt_lpc._celt_lpc(lpc.GetPointer(), ac.GetPointer(), 4);
            for (i = 0; i < 4; i++)
            {
                tmp = Inlines.MULT16_16_Q15(Inlines.QCONST16(.9f, 15), tmp);
                lpc[i] = Inlines.MULT16_16_Q15(lpc[i], tmp);
            }
            /* Add a zero */
            lpc2[0] = (lpc[0] + Inlines.QCONST16(0.8f, CeltConstants.SIG_SHIFT));
            lpc2[1] = (lpc[1] + Inlines.MULT16_16_Q15(c1, lpc[0]));
            lpc2[2] = (lpc[2] + Inlines.MULT16_16_Q15(c1, lpc[1]));
            lpc2[3] = (lpc[3] + Inlines.MULT16_16_Q15(c1, lpc[2]));
            lpc2[4] = Inlines.MULT16_16_Q15(c1, lpc[3]);

            celt_fir5(x_lp, lpc2.GetPointer(), x_lp, len >> 1, mem.GetPointer());
        }

        public static void pitch_search(Pointer<int> x_lp, Pointer<int> y,
                  int len, int max_pitch, BoxedValue<int> pitch, int arch)
        {
            int i, j;
            int lag;
            Pointer<int> best_pitch = new Pointer<int>(new int[] { 0, 0 });
            int maxcorr;
            int xmax, ymax;
            int shift = 0;
            int offset;

            Inlines.OpusAssert(len > 0);
            Inlines.OpusAssert(max_pitch > 0);
            lag = len + max_pitch;

            Pointer<int> x_lp4 = Pointer.Malloc<int>(len >> 2);
            Pointer<int> y_lp4 = Pointer.Malloc<int>(lag >> 2);
            Pointer<int> xcorr = Pointer.Malloc<int>(max_pitch >> 1);

            /* Downsample by 2 again */
            for (j = 0; j < len >> 2; j++)
                x_lp4[j] = x_lp[2 * j];
            for (j = 0; j < lag >> 2; j++)
                y_lp4[j] = y[2 * j];

            xmax = Inlines.celt_maxabs32(x_lp4, len >> 2);
            ymax = Inlines.celt_maxabs32(y_lp4, lag >> 2);
            shift = Inlines.celt_ilog2(Inlines.MAX32(1, Inlines.MAX32(xmax, ymax))) - 11;
            if (shift > 0)
            {
                for (j = 0; j < len >> 2; j++)
                    x_lp4[j] = Inlines.SHR16(x_lp4[j], shift);
                for (j = 0; j < lag >> 2; j++)
                    y_lp4[j] = Inlines.SHR16(y_lp4[j], shift);
                /* Use double the shift for a MAC */
                shift *= 2;
            }
            else {
                shift = 0;
            }

            /* Coarse search with 4x decimation */
            maxcorr =  celt_pitch_xcorr.pitch_xcorr(x_lp4, y_lp4, xcorr, len >> 2, max_pitch >> 2);

            find_best_pitch(xcorr, y_lp4, len >> 2, max_pitch >> 2, best_pitch, 0, maxcorr);

            /* Finer search with 2x decimation */
            maxcorr = 1;
            for (i = 0; i < max_pitch >> 1; i++)
            {
                int sum;
                xcorr[i] = 0;
                if (Inlines.abs(i - 2 * best_pitch[0]) > 2 && Inlines.abs(i - 2 * best_pitch[1]) > 2)
                {
                    continue;
                }
                sum = 0;
                for (j = 0; j < len >> 1; j++)
                    sum += Inlines.SHR32(Inlines.MULT16_16(x_lp[j], y[i + j]), shift);
                
                xcorr[i] = Inlines.MAX32(-1, sum);
                maxcorr = Inlines.MAX32(maxcorr, sum);
            }
            find_best_pitch(xcorr, y, len >> 1, max_pitch >> 1, best_pitch, shift + 1, maxcorr);

            /* Refine by pseudo-interpolation */
            if (best_pitch[0] > 0 && best_pitch[0] < (max_pitch >> 1) - 1)
            {
                int a, b, c;
                a = xcorr[best_pitch[0] - 1];
                b = xcorr[best_pitch[0]];
                c = xcorr[best_pitch[0] + 1];
                if ((c - a) > Inlines.MULT16_32_Q15(Inlines.QCONST16(.7f, 15), b - a))
                {
                    offset = 1;
                }
                else if ((a - c) > Inlines.MULT16_32_Q15(Inlines.QCONST16(.7f, 15), b - c))
                {
                    offset = -1;
                }
                else
                {
                    offset = 0;
                }
            }
            else
            {
                offset = 0;
            }

            pitch.Val = 2 * best_pitch[0] - offset;
        }

        private static readonly int[] second_check = { 0, 0, 3, 2, 3, 2, 5, 2, 3, 2, 3, 2, 5, 2, 3, 2 };

        public static int remove_doubling(Pointer<int> x, int maxperiod, int minperiod,
            int N, BoxedValue<int> T0_, int prev_period, int prev_gain, int arch)
        {
            int k, i, T, T0;
            int g, g0;
            int pg;
            int yy;
            BoxedValue<int> xx = new BoxedValue<int>();
            BoxedValue<int> xy = new BoxedValue<int>();
            BoxedValue<int> xy2 = new BoxedValue<int>();
            int[] xcorr = new int[3];
            int best_xy, best_yy;
            int offset;
            int minperiod0 = minperiod;
            maxperiod /= 2;
            minperiod /= 2;
            T0_.Val /= 2;
            prev_period /= 2;
            N /= 2;
            x = x.Point(maxperiod);
            if (T0_.Val >= maxperiod)
                T0_.Val = maxperiod - 1;

            T = T0 = T0_.Val;
            Pointer<int> yy_lookup = Pointer.Malloc<int>(maxperiod + 1);
            celt_inner_prod.dual_inner_prod_c(x, x, x.Point(0 - T0), N, xx, xy);
            yy_lookup[0] = xx.Val;
            yy = xx.Val;
            for (i = 1; i <= maxperiod; i++)
            {
                yy = yy + Inlines.MULT16_16(x[-i], x[-i]) - Inlines.MULT16_16(x[N - i], x[N - i]);
                yy_lookup[i] = Inlines.MAX32(0, yy);
            }
            yy = yy_lookup[T0];
            best_xy = xy.Val;
            best_yy = yy;

            {
                int x2y2;
                int sh, t;
                x2y2 = 1 + Inlines.HALF32(Inlines.MULT32_32_Q31(xx.Val, yy));
                sh = Inlines.celt_ilog2(x2y2) >> 1;
                t = Inlines.VSHR32(x2y2, 2 * (sh - 7));
                g = (Inlines.VSHR32(Inlines.MULT16_32_Q15(Inlines.celt_rsqrt_norm(t), xy.Val), sh + 1));
                g0 = g;
            }

            /* Look for any pitch at T/k */
            for (k = 2; k <= 15; k++)
            {
                int T1, T1b;
                int g1;
                int cont = 0;
                int thresh;
                T1 = Inlines.celt_udiv(2 * T0 + k, 2 * k);
                if (T1 < minperiod)
                {
                    break;
                }

                /* Look for another strong correlation at T1b */
                if (k == 2)
                {
                    if (T1 + T0 > maxperiod)
                        T1b = T0;
                    else
                        T1b = T0 + T1;
                }
                else
                {
                    T1b = Inlines.celt_udiv(2 * second_check[k] * T0 + k, 2 * k);
                }
                
                celt_inner_prod.dual_inner_prod_c(x, x.Point(0 -T1), x.Point(-T1b), N, xy, xy2);

                xy.Val += xy2.Val;
                yy = yy_lookup[T1] + yy_lookup[T1b];

                {
                    int x2y2;
                    int sh, t;
                    x2y2 = 1 + Inlines.MULT32_32_Q31(xx.Val, yy);
                    sh = Inlines.celt_ilog2(x2y2) >> 1;
                    t = Inlines.VSHR32(x2y2, 2 * (sh - 7));
                    g1 = (Inlines.VSHR32(Inlines.MULT16_32_Q15(Inlines.celt_rsqrt_norm(t), xy.Val), sh + 1));
                }

                if (Inlines.abs(T1 - prev_period) <= 1)
                    cont = prev_gain;
                else if (Inlines.abs(T1 - prev_period) <= 2 && 5 * k * k < T0)
                {
                    cont = Inlines.HALF16(prev_gain); // opus bug: this was half32
                }
                else
                {
                    cont = 0;
                }
                thresh = Inlines.MAX16(Inlines.QCONST16(.3f, 15), (Inlines.MULT16_16_Q15(Inlines.QCONST16(.7f, 15), g0) - cont));

                /* Bias against very high pitch (very short period) to avoid false-positives
                   due to short-term correlation */
                if (T1 < 3 * minperiod)
                {
                    thresh = Inlines.MAX16(Inlines.QCONST16(.4f, 15), (Inlines.MULT16_16_Q15(Inlines.QCONST16(.85f, 15), g0) - cont));
                }
                else if (T1 < 2 * minperiod)
                {
                    thresh = Inlines.MAX16(Inlines.QCONST16(.5f, 15), (Inlines.MULT16_16_Q15(Inlines.QCONST16(.9f, 15), g0) - cont));
                }
                if (g1 > thresh)
                {
                    best_xy = xy.Val;
                    best_yy = yy;
                    T = T1;
                    g = g1;
                }
            }

            best_xy = Inlines.MAX32(0, best_xy);
            if (best_yy <= best_xy)
            {
                pg = CeltConstants.Q15ONE;
            }
            else
            {
                pg = (Inlines.SHR32(Inlines.frac_div32(best_xy, best_yy + 1), 16));
            }

            for (k = 0; k < 3; k++)
            {
                xcorr[k] = celt_inner_prod.celt_inner_prod_c(x, x.Point(0 - (T + k - 1)), N);
            }

            if ((xcorr[2] - xcorr[0]) > Inlines.MULT16_32_Q15(Inlines.QCONST16(.7f, 15), xcorr[1] - xcorr[0]))
            {
                offset = 1;
            }
            else if ((xcorr[0] - xcorr[2]) > Inlines.MULT16_32_Q15(Inlines.QCONST16(.7f, 15), xcorr[1] - xcorr[2]))
            {
                offset = -1;
            }
            else
            {
                offset = 0;
            }

            if (pg > g)
            {
                pg = g;
            }

            T0_.Val = 2 * T + offset;

            if (T0_.Val < minperiod0)
            {
                T0_.Val = minperiod0;
            }

            return pg;
        }

    }
}