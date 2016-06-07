﻿using Concentus.Common;
using Concentus.Common.CPlusPlus;
using Concentus.Silk.Enums;
using Concentus.Silk.Structs;
using System;
using System.Diagnostics;


namespace Concentus.Silk
{
    /// <summary>
    /// Routines for managing packet loss concealment
    /// </summary>
    public static class PLC
    {
        private const int NB_ATT = 2;
        private static readonly short[] HARM_ATT_Q15 = { 32440, 31130 }; /* 0.99, 0.95 */
        private static readonly short[] PLC_RAND_ATTENUATE_V_Q15 = { 31130, 26214 }; /* 0.95, 0.8 */
        private static readonly short[] PLC_RAND_ATTENUATE_UV_Q15 = { 32440, 29491 }; /* 0.99, 0.9 */

        public static void silk_PLC_Reset(
            silk_decoder_state psDec              /* I/O Decoder state        */
        )
        {
            psDec.sPLC.pitchL_Q8 = Inlines.silk_LSHIFT(psDec.frame_length, 8 - 1);
            psDec.sPLC.prevGain_Q16[0] = Inlines.SILK_FIX_CONST(1, 16);
            psDec.sPLC.prevGain_Q16[1] = Inlines.SILK_FIX_CONST(1, 16);
            psDec.sPLC.subfr_length = 20;
            psDec.sPLC.nb_subfr = 2;
        }

        public static void silk_PLC(
            silk_decoder_state psDec,             /* I/O Decoder state        */
            silk_decoder_control psDecCtrl,         /* I/O Decoder control      */
            Pointer<short> frame,            /* I/O  signal              */
            int lost,               /* I Loss flag              */
            int arch                /* I Run-time architecture  */
        )
        {
            /* PLC control function */
            if (psDec.fs_kHz != psDec.sPLC.fs_kHz)
            {
                silk_PLC_Reset(psDec);
                psDec.sPLC.fs_kHz = psDec.fs_kHz;
            }

            if (lost != 0)
            {
                /****************************/
                /* Generate Signal          */
                /****************************/
                silk_PLC_conceal(psDec, psDecCtrl, frame, arch);

                psDec.lossCnt++;
            }
            else {
                /****************************/
                /* Update state             */
                /****************************/
                silk_PLC_update(psDec, psDecCtrl);
            }
        }

        /**************************************************/
        /* Update state of PLC                            */
        /**************************************************/
        public static void silk_PLC_update(
            silk_decoder_state psDec,             /* I/O Decoder state        */
            silk_decoder_control psDecCtrl          /* I/O Decoder control      */
        )
        {
            int LTP_Gain_Q14, temp_LTP_Gain_Q14;
            int i, j;
            silk_PLC_struct psPLC = psDec.sPLC; // [porting note] pointer on the stack

            /* Update parameters used in case of packet loss */
            psDec.prevSignalType = psDec.indices.signalType;
            LTP_Gain_Q14 = 0;
            if (psDec.indices.signalType == SilkConstants.TYPE_VOICED)
            {
                /* Find the parameters for the last subframe which contains a pitch pulse */
                for (j = 0; j * psDec.subfr_length < psDecCtrl.pitchL[psDec.nb_subfr - 1]; j++)
                {
                    if (j == psDec.nb_subfr)
                    {
                        break;
                    }
                    temp_LTP_Gain_Q14 = 0;
                    for (i = 0; i < SilkConstants.LTP_ORDER; i++)
                    {
                        temp_LTP_Gain_Q14 += psDecCtrl.LTPCoef_Q14[(psDec.nb_subfr - 1 - j) * SilkConstants.LTP_ORDER + i];
                    }
                    if (temp_LTP_Gain_Q14 > LTP_Gain_Q14)
                    {
                        LTP_Gain_Q14 = temp_LTP_Gain_Q14;

                        psDecCtrl.LTPCoef_Q14.Point(Inlines.silk_SMULBB(psDec.nb_subfr - 1 - j, SilkConstants.LTP_ORDER)).MemCopyTo(psPLC.LTPCoef_Q14, SilkConstants.LTP_ORDER);

                        psPLC.pitchL_Q8 = Inlines.silk_LSHIFT(psDecCtrl.pitchL[psDec.nb_subfr - 1 - j], 8);
                    }
                }

                psPLC.LTPCoef_Q14.MemSet(0, SilkConstants.LTP_ORDER);
                psPLC.LTPCoef_Q14[SilkConstants.LTP_ORDER / 2] = Inlines.CHOP16(LTP_Gain_Q14);

                /* Limit LT coefs */
                if (LTP_Gain_Q14 < SilkConstants.V_PITCH_GAIN_START_MIN_Q14)
                {
                    int scale_Q10;
                    int tmp;

                    tmp = Inlines.silk_LSHIFT(SilkConstants.V_PITCH_GAIN_START_MIN_Q14, 10);
                    scale_Q10 = Inlines.silk_DIV32(tmp, Inlines.silk_max(LTP_Gain_Q14, 1));
                    for (i = 0; i < SilkConstants.LTP_ORDER; i++)
                    {
                        psPLC.LTPCoef_Q14[i] = Inlines.CHOP16(Inlines.silk_RSHIFT(Inlines.silk_SMULBB(psPLC.LTPCoef_Q14[i], scale_Q10), 10));
                    }
                }
                else if (LTP_Gain_Q14 > SilkConstants.V_PITCH_GAIN_START_MAX_Q14)
                {
                    int scale_Q14;
                    int tmp;

                    tmp = Inlines.silk_LSHIFT(SilkConstants.V_PITCH_GAIN_START_MAX_Q14, 14);
                    scale_Q14 = Inlines.silk_DIV32(tmp, Inlines.silk_max(LTP_Gain_Q14, 1));
                    for (i = 0; i < SilkConstants.LTP_ORDER; i++)
                    {
                        psPLC.LTPCoef_Q14[i] = Inlines.CHOP16(Inlines.silk_RSHIFT(Inlines.silk_SMULBB(psPLC.LTPCoef_Q14[i], scale_Q14), 14));
                    }
                }
            }
            else {
                psPLC.pitchL_Q8 = Inlines.silk_LSHIFT(Inlines.silk_SMULBB(psDec.fs_kHz, 18), 8);
                psPLC.LTPCoef_Q14.MemSet(0, SilkConstants.LTP_ORDER);
            }

            /* Save LPC coeficients */
            psDecCtrl.PredCoef_Q12[1].MemCopyTo(psPLC.prevLPC_Q12, psDec.LPC_order);
            psPLC.prevLTP_scale_Q14 = Inlines.CHOP16(psDecCtrl.LTP_scale_Q14);

            /* Save last two gains */
            psDecCtrl.Gains_Q16.Point(psDec.nb_subfr - 2).MemCopyTo(psPLC.prevGain_Q16, 2);

            psPLC.subfr_length = psDec.subfr_length;
            psPLC.nb_subfr = psDec.nb_subfr;
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="energy1">O</param>
        /// <param name="shift1">O</param>
        /// <param name="energy2">O</param>
        /// <param name="shift2">O</param>
        /// <param name="exc_Q14">I</param>
        /// <param name="prevGain_Q10">I</param>
        /// <param name="subfr_length">I</param>
        /// <param name="nb_subfr">I</param>
        public static void silk_PLC_energy(
            BoxedValue<int> energy1,
            BoxedValue<int> shift1,
            BoxedValue<int> energy2,
            BoxedValue<int> shift2,
            Pointer<int> exc_Q14,
            Pointer<int> prevGain_Q10,
            int subfr_length,
            int nb_subfr)
        {
            int i, k;
            Pointer<short> exc_buf_ptr;
            Pointer<short> exc_buf = Pointer.Malloc<short>(2 * subfr_length);

            /* Find random noise component */
            /* Scale previous excitation signal */
            exc_buf_ptr = exc_buf;
            for (k = 0; k < 2; k++)
            {
                for (i = 0; i < subfr_length; i++)
                {
                    exc_buf_ptr[i] = (short)Inlines.silk_SAT16(Inlines.silk_RSHIFT(
                        Inlines.silk_SMULWW(exc_Q14[i + (k + nb_subfr - 2) * subfr_length], prevGain_Q10[k]), 8));
                }
                exc_buf_ptr = exc_buf_ptr.Point(subfr_length);
            }

            /* Find the subframe with lowest energy of the last two and use that as random noise generator */
            SumSqrShift.silk_sum_sqr_shift(energy1, shift1, exc_buf, subfr_length);
            SumSqrShift.silk_sum_sqr_shift(energy2, shift2, exc_buf.Point(subfr_length), subfr_length);
        }

        public static void silk_PLC_conceal(
            silk_decoder_state psDec,             /* I/O Decoder state        */
            silk_decoder_control psDecCtrl,         /* I/O Decoder control      */
            Pointer<short> frame,            /* O LPC residual signal    */
            int arch                /* I Run-time architecture  */
        )
        {
            int i, j, k;
            int lag, idx, sLTP_buf_idx;
            int rand_seed, harm_Gain_Q15, rand_Gain_Q15, inv_gain_Q30;
            BoxedValue<int> energy1 = new BoxedValue<int>();
            BoxedValue<int> energy2 = new BoxedValue<int>();
            BoxedValue<int> shift1 = new BoxedValue<int>();
            BoxedValue<int> shift2 = new BoxedValue<int>();
            Pointer<int> rand_ptr;
            Pointer<int> pred_lag_ptr;
            int LPC_pred_Q10, LTP_pred_Q12;
            short rand_scale_Q14;
            Pointer<short> B_Q14;
            Pointer<int> sLPC_Q14_ptr;
            Pointer<short> A_Q12 = Pointer.Malloc<short>(SilkConstants.MAX_LPC_ORDER);
            Pointer<short> sLTP = Pointer.Malloc<short>(psDec.ltp_mem_length);
            Pointer<int> sLTP_Q14 = Pointer.Malloc<int>(psDec.ltp_mem_length + psDec.frame_length);
            silk_PLC_struct psPLC = psDec.sPLC;
            Pointer<int> prevGain_Q10 = Pointer.Malloc<int>(2);

            prevGain_Q10[0] = Inlines.silk_RSHIFT(psPLC.prevGain_Q16[0], 6);
            prevGain_Q10[1] = Inlines.silk_RSHIFT(psPLC.prevGain_Q16[1], 6);

            if (psDec.first_frame_after_reset != 0)
            {
                psPLC.prevLPC_Q12.MemSet(0, SilkConstants.MAX_LPC_ORDER);
            }

            silk_PLC_energy(energy1, shift1, energy2, shift2, psDec.exc_Q14, prevGain_Q10, psDec.subfr_length, psDec.nb_subfr);

            if (Inlines.silk_RSHIFT(energy1.Val, shift2.Val) < Inlines.silk_RSHIFT(energy2.Val, shift1.Val))
            {
                /* First sub-frame has lowest energy */
                rand_ptr = psDec.exc_Q14.Point(Inlines.silk_max_int(0, (psPLC.nb_subfr - 1) * psPLC.subfr_length - SilkConstants.RAND_BUF_SIZE));
            }
            else {
                /* Second sub-frame has lowest energy */
                rand_ptr = psDec.exc_Q14.Point(Inlines.silk_max_int(0, psPLC.nb_subfr * psPLC.subfr_length - SilkConstants.RAND_BUF_SIZE));
            }

            /* Set up Gain to random noise component */
            B_Q14 = psPLC.LTPCoef_Q14;
            rand_scale_Q14 = psPLC.randScale_Q14;

            /* Set up attenuation gains */
            harm_Gain_Q15 = HARM_ATT_Q15[Inlines.silk_min_int(NB_ATT - 1, psDec.lossCnt)];
            if (psDec.prevSignalType == SilkConstants.TYPE_VOICED)
            {
                rand_Gain_Q15 = PLC_RAND_ATTENUATE_V_Q15[Inlines.silk_min_int(NB_ATT - 1, psDec.lossCnt)];
            }
            else {
                rand_Gain_Q15 = PLC_RAND_ATTENUATE_UV_Q15[Inlines.silk_min_int(NB_ATT - 1, psDec.lossCnt)];
            }

            /* LPC concealment. Apply BWE to previous LPC */
            bwexpander.silk_bwexpander(psPLC.prevLPC_Q12, psDec.LPC_order, Inlines.SILK_FIX_CONST(SilkConstants.BWE_COEF, 16));

            /* Preload LPC coeficients to array on stack. Gives small performance gain FIXME no it doesn't */
            psPLC.prevLPC_Q12.MemCopyTo(A_Q12, psDec.LPC_order);

            /* First Lost frame */
            if (psDec.lossCnt == 0)
            {
                rand_scale_Q14 = 1 << 14;

                /* Reduce random noise Gain for voiced frames */
                if (psDec.prevSignalType == SilkConstants.TYPE_VOICED)
                {
                    for (i = 0; i < SilkConstants.LTP_ORDER; i++)
                    {
                        rand_scale_Q14 -= B_Q14[i];
                    }
                    rand_scale_Q14 = Inlines.silk_max_16(3277, rand_scale_Q14); /* 0.2 */
                    rand_scale_Q14 = (short)Inlines.silk_RSHIFT(Inlines.silk_SMULBB(rand_scale_Q14, psPLC.prevLTP_scale_Q14), 14);
                }
                else
                {
                    /* Reduce random noise for unvoiced frames with high LPC gain */
                    int invGain_Q30, down_scale_Q30;

                    invGain_Q30 = LPC_inv_pred_gain.silk_LPC_inverse_pred_gain(psPLC.prevLPC_Q12, psDec.LPC_order);

                    down_scale_Q30 = Inlines.silk_min_32(Inlines.silk_RSHIFT((int)1 << 30, SilkConstants.LOG2_INV_LPC_GAIN_HIGH_THRES), invGain_Q30);
                    down_scale_Q30 = Inlines.silk_max_32(Inlines.silk_RSHIFT((int)1 << 30, SilkConstants.LOG2_INV_LPC_GAIN_LOW_THRES), down_scale_Q30);
                    down_scale_Q30 = Inlines.silk_LSHIFT(down_scale_Q30, SilkConstants.LOG2_INV_LPC_GAIN_HIGH_THRES);

                    rand_Gain_Q15 = Inlines.silk_RSHIFT(Inlines.silk_SMULWB(down_scale_Q30, rand_Gain_Q15), 14);
                }
            }

            rand_seed = psPLC.rand_seed;
            lag = Inlines.silk_RSHIFT_ROUND(psPLC.pitchL_Q8, 8);
            sLTP_buf_idx = psDec.ltp_mem_length;

            /* Rewhiten LTP state */
            idx = psDec.ltp_mem_length - lag - psDec.LPC_order - SilkConstants.LTP_ORDER / 2;
            Inlines.OpusAssert(idx > 0);
            Filters.silk_LPC_analysis_filter(sLTP.Point(idx), psDec.outBuf.Point(idx), A_Q12, psDec.ltp_mem_length - idx, psDec.LPC_order, arch);
            /* Scale LTP state */
            inv_gain_Q30 = Inlines.silk_INVERSE32_varQ(psPLC.prevGain_Q16[1], 46);
            inv_gain_Q30 = Inlines.silk_min(inv_gain_Q30, int.MaxValue >> 1);
            for (i = idx + psDec.LPC_order; i < psDec.ltp_mem_length; i++)
            {
                sLTP_Q14[i] = Inlines.silk_SMULWB(inv_gain_Q30, sLTP[i]);
            }

            /***************************/
            /* LTP synthesis filtering */
            /***************************/
            for (k = 0; k < psDec.nb_subfr; k++)
            {
                /* Set up pointer */
                pred_lag_ptr = sLTP_Q14.Point(sLTP_buf_idx - lag + SilkConstants.LTP_ORDER / 2);
                for (i = 0; i < psDec.subfr_length; i++)
                {
                    /* Unrolled loop */
                    /* Avoids introducing a bias because Inlines.silk_SMLAWB() always rounds to -inf */
                    LTP_pred_Q12 = 2;
                    LTP_pred_Q12 = Inlines.silk_SMLAWB(LTP_pred_Q12, pred_lag_ptr[0], B_Q14[0]);
                    LTP_pred_Q12 = Inlines.silk_SMLAWB(LTP_pred_Q12, pred_lag_ptr[-1], B_Q14[1]);
                    LTP_pred_Q12 = Inlines.silk_SMLAWB(LTP_pred_Q12, pred_lag_ptr[-2], B_Q14[2]);
                    LTP_pred_Q12 = Inlines.silk_SMLAWB(LTP_pred_Q12, pred_lag_ptr[-3], B_Q14[3]);
                    LTP_pred_Q12 = Inlines.silk_SMLAWB(LTP_pred_Q12, pred_lag_ptr[-4], B_Q14[4]);
                    pred_lag_ptr = pred_lag_ptr.Point(1);

                    /* Generate LPC excitation */
                    rand_seed = Inlines.silk_RAND(rand_seed);
                    idx = Inlines.silk_RSHIFT(rand_seed, 25) & SilkConstants.RAND_BUF_MASK;
                    sLTP_Q14[sLTP_buf_idx] = Inlines.silk_LSHIFT32(Inlines.silk_SMLAWB(LTP_pred_Q12, rand_ptr[idx], rand_scale_Q14), 2);
                    sLTP_buf_idx++;
                }

                /* Gradually reduce LTP gain */
                for (j = 0; j < SilkConstants.LTP_ORDER; j++)
                {
                    B_Q14[j] = Inlines.CHOP16(Inlines.silk_RSHIFT(Inlines.silk_SMULBB(harm_Gain_Q15, B_Q14[j]), 15));
                }
                /* Gradually reduce excitation gain */
                rand_scale_Q14 = Inlines.CHOP16(Inlines.silk_RSHIFT(Inlines.silk_SMULBB(rand_scale_Q14, rand_Gain_Q15), 15));

                /* Slowly increase pitch lag */
                psPLC.pitchL_Q8 = Inlines.silk_SMLAWB(psPLC.pitchL_Q8, psPLC.pitchL_Q8, SilkConstants.PITCH_DRIFT_FAC_Q16);
                psPLC.pitchL_Q8 = Inlines.silk_min_32(psPLC.pitchL_Q8, Inlines.silk_LSHIFT(Inlines.silk_SMULBB(SilkConstants.MAX_PITCH_LAG_MS, psDec.fs_kHz), 8));
                lag = Inlines.silk_RSHIFT_ROUND(psPLC.pitchL_Q8, 8);
            }

            /***************************/
            /* LPC synthesis filtering */
            /***************************/
            sLPC_Q14_ptr = sLTP_Q14.Point(psDec.ltp_mem_length - SilkConstants.MAX_LPC_ORDER);

            /* Copy LPC state */
            psDec.sLPC_Q14_buf.MemCopyTo(sLPC_Q14_ptr, SilkConstants.MAX_LPC_ORDER);

            Inlines.OpusAssert(psDec.LPC_order >= 10); /* check that unrolling works */
            for (i = 0; i < psDec.frame_length; i++)
            {
                /* partly unrolled */
                /* Avoids introducing a bias because Inlines.silk_SMLAWB() always rounds to -inf */
                LPC_pred_Q10 = Inlines.silk_RSHIFT(psDec.LPC_order, 1);
                LPC_pred_Q10 = Inlines.silk_SMLAWB(LPC_pred_Q10, sLPC_Q14_ptr[SilkConstants.MAX_LPC_ORDER + i - 1], A_Q12[0]);
                LPC_pred_Q10 = Inlines.silk_SMLAWB(LPC_pred_Q10, sLPC_Q14_ptr[SilkConstants.MAX_LPC_ORDER + i - 2], A_Q12[1]);
                LPC_pred_Q10 = Inlines.silk_SMLAWB(LPC_pred_Q10, sLPC_Q14_ptr[SilkConstants.MAX_LPC_ORDER + i - 3], A_Q12[2]);
                LPC_pred_Q10 = Inlines.silk_SMLAWB(LPC_pred_Q10, sLPC_Q14_ptr[SilkConstants.MAX_LPC_ORDER + i - 4], A_Q12[3]);
                LPC_pred_Q10 = Inlines.silk_SMLAWB(LPC_pred_Q10, sLPC_Q14_ptr[SilkConstants.MAX_LPC_ORDER + i - 5], A_Q12[4]);
                LPC_pred_Q10 = Inlines.silk_SMLAWB(LPC_pred_Q10, sLPC_Q14_ptr[SilkConstants.MAX_LPC_ORDER + i - 6], A_Q12[5]);
                LPC_pred_Q10 = Inlines.silk_SMLAWB(LPC_pred_Q10, sLPC_Q14_ptr[SilkConstants.MAX_LPC_ORDER + i - 7], A_Q12[6]);
                LPC_pred_Q10 = Inlines.silk_SMLAWB(LPC_pred_Q10, sLPC_Q14_ptr[SilkConstants.MAX_LPC_ORDER + i - 8], A_Q12[7]);
                LPC_pred_Q10 = Inlines.silk_SMLAWB(LPC_pred_Q10, sLPC_Q14_ptr[SilkConstants.MAX_LPC_ORDER + i - 9], A_Q12[8]);
                LPC_pred_Q10 = Inlines.silk_SMLAWB(LPC_pred_Q10, sLPC_Q14_ptr[SilkConstants.MAX_LPC_ORDER + i - 10], A_Q12[9]);
                for (j = 10; j < psDec.LPC_order; j++)
                {
                    LPC_pred_Q10 = Inlines.silk_SMLAWB(LPC_pred_Q10, sLPC_Q14_ptr[SilkConstants.MAX_LPC_ORDER + i - j - 1], A_Q12[j]);
                }

                /* Add prediction to LPC excitation */
                sLPC_Q14_ptr[SilkConstants.MAX_LPC_ORDER + i] = Inlines.silk_ADD_LSHIFT32(sLPC_Q14_ptr[SilkConstants.MAX_LPC_ORDER + i], LPC_pred_Q10, 4);

                /* Scale with Gain */
                frame[i] = (short)Inlines.silk_SAT16(Inlines.silk_SAT16(Inlines.silk_RSHIFT_ROUND(Inlines.silk_SMULWW(sLPC_Q14_ptr[SilkConstants.MAX_LPC_ORDER + i], prevGain_Q10[1]), 8)));
            }

            /* Save LPC state */
            sLPC_Q14_ptr.Point(psDec.frame_length).MemCopyTo(psDec.sLPC_Q14_buf, SilkConstants.MAX_LPC_ORDER);

            /**************************************/
            /* Update states                      */
            /**************************************/
            psPLC.rand_seed = rand_seed;
            psPLC.randScale_Q14 = rand_scale_Q14;
            for (i = 0; i < SilkConstants.MAX_NB_SUBFR; i++)
            {
                psDecCtrl.pitchL[i] = lag;
            }
        }

        /* Glues concealed frames with new good received frames */
        public static void silk_PLC_glue_frames(
            silk_decoder_state psDec,             /* I/O decoder state        */
            Pointer<short> frame,            /* I/O signal               */
            int length              /* I length of signal       */
        )
        {
            int i;
            BoxedValue<int> energy_shift = new BoxedValue<int>();
            BoxedValue<int> energy = new BoxedValue<int>();
            silk_PLC_struct psPLC = psDec.sPLC;

            if (psDec.lossCnt != 0)
            {
                /* Calculate energy in concealed residual */
                BoxedValue<int> boxedEnergy = new BoxedValue<int>(psPLC.conc_energy);
                BoxedValue<int> boxedShift = new BoxedValue<int>(psPLC.conc_energy_shift);
                SumSqrShift.silk_sum_sqr_shift(boxedEnergy, boxedShift, frame, length);
                psPLC.conc_energy = boxedEnergy.Val;
                psPLC.conc_energy_shift = boxedShift.Val;

                psPLC.last_frame_lost = 1;
            }
            else
            {
                if (psDec.sPLC.last_frame_lost != 0)
                {
                    /* Calculate residual in decoded signal if last frame was lost */
                    SumSqrShift.silk_sum_sqr_shift(energy, energy_shift, frame, length);

                    /* Normalize energies */
                    if (energy_shift.Val > psPLC.conc_energy_shift)
                    {
                        psPLC.conc_energy = Inlines.silk_RSHIFT(psPLC.conc_energy, energy_shift.Val - psPLC.conc_energy_shift);
                    }
                    else if (energy_shift.Val < psPLC.conc_energy_shift)
                    {
                        energy.Val = Inlines.silk_RSHIFT(energy.Val, psPLC.conc_energy_shift - energy_shift.Val);
                    }

                    /* Fade in the energy difference */
                    if (energy.Val > psPLC.conc_energy)
                    {
                        int frac_Q24, LZ;
                        int gain_Q16, slope_Q16;

                        LZ = Inlines.silk_CLZ32(psPLC.conc_energy);
                        LZ = LZ - 1;
                        psPLC.conc_energy = Inlines.silk_LSHIFT(psPLC.conc_energy, LZ);
                        energy.Val = Inlines.silk_RSHIFT(energy.Val, Inlines.silk_max_32(24 - LZ, 0));

                        frac_Q24 = Inlines.silk_DIV32(psPLC.conc_energy, Inlines.silk_max(energy.Val, 1));

                        gain_Q16 = Inlines.silk_LSHIFT(Inlines.silk_SQRT_APPROX(frac_Q24), 4);
                        slope_Q16 = Inlines.silk_DIV32_16(((int)1 << 16) - gain_Q16, length);
                        /* Make slope 4x steeper to avoid missing onsets after DTX */
                        slope_Q16 = Inlines.silk_LSHIFT(slope_Q16, 2);

                        for (i = 0; i < length; i++)
                        {
                            frame[i] = Inlines.CHOP16(Inlines.silk_SMULWB(gain_Q16, frame[i]));
                            gain_Q16 += slope_Q16;
                            if (gain_Q16 > (int)1 << 16)
                            {
                                break;
                            }
                        }
                    }
                }
                psPLC.last_frame_lost = 0;
            }
        }
    }
}